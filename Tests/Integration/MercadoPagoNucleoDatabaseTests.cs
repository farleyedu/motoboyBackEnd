using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.MercadoPago;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// Nucleo Mercado Pago contra PostgreSQL real (TEST_DELIVERY_DATABASE) e um MP falso em memoria (mesmo contrato HTTP da API v1).
/// Prova: Pix criado na conta da loja com idempotencia, webhook duplicado sem efeito duplo, evento fora de ordem sem retrocesso,
/// cobranca do chat acompanhando, estorno limitado ao pago e conciliador recuperando pagamento sem resposta.
/// </summary>
public sealed class MercadoPagoNucleoDatabaseTests
{
    private const string WebhookSecret = "whsec-teste";
    private const long MpUserLoja = 555001;

    [DeliveryDatabaseFact]
    public async Task PixDoCardapioNaContaDaLojaWebhookIdempotenteEForaDeOrdem()
    {
        await using var db = await Banco.Criar();
        var pix = db.Get<IPixChargeService>();

        var pedido = new PixChargeRequest
        {
            IdEstabelecimento = db.Loja, Origem = OrigensPagamento.Cardapio, Valor = 42.90m, Descricao = "Pedido ABC", IdempotencyKey = "cardapio:1",
            PrePedidoId = db.PrePedido, GerarLinkPublico = true
        };
        var r1 = await pix.CriarAsync(pedido);
        Assert.Equal(StatusIntent.AguardandoPagamento, r1.Status);
        Assert.False(string.IsNullOrEmpty(r1.CopiaECola));
        Assert.False(string.IsNullOrEmpty(r1.TokenPublico));
        Assert.StartsWith("zp_s_cardapio_", r1.ExternalReference);

        var chamada = Assert.Single(db.Mp.Criacoes);
        Assert.Equal("Bearer TOKEN-DA-LOJA", chamada.Authorization);
        Assert.False(string.IsNullOrEmpty(chamada.Idempotencia));
        Assert.Equal(42.90m, MpOrdersFalso.Valor(chamada.Corpo));
        Assert.Equal(r1.ExternalReference, chamada.Corpo["external_reference"]!.GetValue<string>());

        // Mesma chave: devolve o mesmo Pix, sem nova chamada ao MP.
        var r2 = await pix.CriarAsync(pedido);
        Assert.True(r2.Reaproveitado);
        Assert.Equal(r1.MpPaymentId, r2.MpPaymentId);
        Assert.Single(db.Mp.Criacoes);
        // Mesma chave com outro valor: conflito.
        var conflito = await Assert.ThrowsAsync<PagamentoException>(() => pix.CriarAsync(new PixChargeRequest
        {
            IdEstabelecimento = db.Loja, Origem = OrigensPagamento.Cardapio, Valor = 1m, Descricao = "x", IdempotencyKey = "cardapio:1", PrePedidoId = db.PrePedido
        }));
        Assert.Equal(409, conflito.Http);
        Assert.Equal("aguardando", await db.Scalar<string>("SELECT status_pagamento FROM cardapio_pedido_publico"));

        // Cliente paga; o MP avisa.
        var t1 = DateTimeOffset.UtcNow;
        db.Mp.Mudar(r1.MpPaymentId!.Value, "approved", "accredited", t1);
        var webhook = db.Get<IMercadoPagoWebhookService>();
        var aviso = db.Aviso(r1.MpPaymentId!.Value, notificacao: "n1");
        var w1 = await webhook.ReceberAsync(aviso);
        Assert.Equal(WebhookDesfecho.Aceito, w1.Desfecho);
        Assert.Equal("processado", w1.StatusProcessamento);
        Assert.Equal(StatusIntent.Pago, await db.Scalar<string>("SELECT status FROM pagamento_intent"));
        Assert.Equal("pago", await db.Scalar<string>("SELECT status_pagamento FROM cardapio_pedido_publico"));
        var outbox = await db.Scalar<long>("SELECT count(*) FROM pagamento_outbox");
        var auditoria = await db.Scalar<long>("SELECT count(*) FROM pagamento_auditoria");

        // Reenvio do mesmo aviso: barrado na entrada.
        Assert.Equal(WebhookDesfecho.Duplicado, (await webhook.ReceberAsync(db.Aviso(r1.MpPaymentId!.Value, notificacao: "n1"))).Desfecho);
        // Outro aviso do mesmo pagamento (payment.updated): processado, sem efeito novo.
        Assert.Equal("processado", (await webhook.ReceberAsync(db.Aviso(r1.MpPaymentId!.Value, notificacao: "n2"))).StatusProcessamento);
        Assert.Equal(outbox, await db.Scalar<long>("SELECT count(*) FROM pagamento_outbox"));
        Assert.Equal(1L, await db.Scalar<long>("SELECT count(*) FROM pagamento"));

        // Foto velha (pendente, data anterior) chegando depois: nao retrocede.
        var sync = db.Get<IPagamentoSincronizador>();
        var cred = await db.Get<IMercadoPagoCredencialResolver>().LojaAsync(db.Loja);
        var velho = db.Mp.Copia(r1.MpPaymentId!.Value);
        velho.Status = "pending";
        velho.DateLastUpdated = t1.AddMinutes(-5);
        Assert.Equal(ResultadoSincronizacao.SemMudanca, (await sync.AplicarAsync(cred, velho, null, "webhook")).Resultado);
        Assert.Equal(StatusIntent.Pago, await db.Scalar<string>("SELECT status FROM pagamento_intent"));
        Assert.Equal(outbox, await db.Scalar<long>("SELECT count(*) FROM pagamento_outbox"));

        // Assinatura invalida: 401 e nada gravado.
        var ruim = db.Aviso(r1.MpPaymentId!.Value, notificacao: "n3");
        ruim.Assinatura = ruim.Assinatura!.Replace("v1=", "v1=0");
        Assert.Equal(WebhookDesfecho.NaoAutorizado, (await webhook.ReceberAsync(ruim)).Desfecho);
        Assert.Equal(2L, await db.Scalar<long>("SELECT count(*) FROM mp_webhook_evento"));

        // Pagina publica so com o token, sem dado do cliente.
        Assert.Equal(1L, await db.Scalar<long>($"SELECT count(*) FROM pagamento_intent WHERE token_publico_hash = '{TokenPublico.Hash(r1.TokenPublico!)}'"));
        Assert.True(auditoria > 0);
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute("DELETE FROM pagamento_auditoria"));
    }

    [DeliveryDatabaseFact]
    public async Task CobrancaDoChatAcompanhaPixEEstornoNuncaPassaDoPago()
    {
        await using var db = await Banco.Criar();
        var cobranca = Guid.NewGuid();
        var pix = await db.Get<IPixChargeService>().CriarAsync(new PixChargeRequest
        {
            IdEstabelecimento = db.Loja, Origem = OrigensPagamento.Chat, Valor = 30m, Descricao = "Taxa extra", IdempotencyKey = $"chat:{cobranca:N}",
            ConversaId = db.Conversa
        });
        await db.Execute(@"
INSERT INTO cobranca_chat (id, id_estabelecimento, conversa_id, id_intent, autor_usuario_id, descricao, valor, tipo, status, idempotency_key)
VALUES (@C, @L, @Conv, @I, 7, 'Taxa extra', 30, 'avulsa', 'criando', 'k1')", new { C = cobranca, L = db.Loja, Conv = db.Conversa, I = pix.IntentId });

        db.Mp.Mudar(pix.MpPaymentId!.Value, "approved", "accredited", DateTimeOffset.UtcNow);
        await db.Get<IMercadoPagoWebhookService>().ReceberAsync(db.Aviso(pix.MpPaymentId!.Value, "c1"));
        Assert.Equal(StatusCobranca.Pago, await db.Scalar<string>("SELECT status FROM cobranca_chat"));
        Assert.True(await db.Scalar<long>("SELECT count(*) FROM pagamento_outbox WHERE evento = 'cobrancaAtualizada'") >= 1);
        var payload = await db.Scalar<string>("SELECT payload::text FROM pagamento_outbox WHERE evento = 'cobrancaAtualizada' ORDER BY id DESC LIMIT 1");
        Assert.Contains("\"conversaId\"", payload);
        Assert.DoesNotContain("TOKEN", payload);

        // Cartao no chat e proibido.
        var cartao = await Assert.ThrowsAsync<PagamentoException>(() => db.Get<ICardPaymentService>().CriarAsync(new CardPaymentRequest
        {
            IdEstabelecimento = db.Loja, Origem = OrigensPagamento.Chat, Valor = 30m, Descricao = "x", IdempotencyKey = "chat:cartao", Token = "t", PaymentMethodId = "visa"
        }));
        Assert.Equal(PagamentoCodigos.CartaoNaoPermitidoNoChat, cartao.Codigo);

        var estornos = db.Get<IPagamentoEstornoService>();
        var parcial = await estornos.EstornarAsync(new EstornoRequest { IntentId = pix.IntentId, IdEstabelecimento = db.Loja, Valor = 10m, IdempotencyKey = "e1", UsuarioId = 7 });
        Assert.Equal(StatusIntent.EstornadoParcial, parcial.StatusIntent);
        // Repetir a mesma chave nao estorna de novo.
        await estornos.EstornarAsync(new EstornoRequest { IntentId = pix.IntentId, IdEstabelecimento = db.Loja, Valor = 10m, IdempotencyKey = "e1" });
        Assert.Single(db.Mp.Estornos);

        var acima = await Assert.ThrowsAsync<PagamentoException>(() =>
            estornos.EstornarAsync(new EstornoRequest { IntentId = pix.IntentId, IdEstabelecimento = db.Loja, Valor = 25m, IdempotencyKey = "e2" }));
        Assert.Equal(PagamentoCodigos.EstornoAcimaDoPago, acima.Codigo);
        // O banco barra mesmo se a aplicacao errar a conta.
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute(@"
INSERT INTO pagamento_estorno (id, id_pagamento, id_estabelecimento, valor, idempotency_key)
SELECT gen_random_uuid(), id, id_estabelecimento, 25, 'manual' FROM pagamento"));

        var resto = await estornos.EstornarAsync(new EstornoRequest { IntentId = pix.IntentId, IdEstabelecimento = db.Loja, IdempotencyKey = "e3" });
        Assert.Equal(20m, resto.Valor);
        Assert.Equal(StatusIntent.Estornado, resto.StatusIntent);
        Assert.Equal(StatusCobranca.Reembolsado, await db.Scalar<string>("SELECT status FROM cobranca_chat"));
        Assert.Equal(30m, await db.Scalar<decimal>("SELECT valor_estornado FROM pagamento_intent"));
    }

    [DeliveryDatabaseFact]
    public async Task ConciliadorRecuperaPixSemRespostaETitularidadeErradaERecusada()
    {
        await using var db = await Banco.Criar();
        var pix = db.Get<IPixChargeService>();
        db.Mp.PerderProximaResposta = true;
        var semResposta = await Assert.ThrowsAsync<PagamentoException>(() => pix.CriarAsync(new PixChargeRequest
        {
            IdEstabelecimento = db.Loja, Origem = OrigensPagamento.Cardapio, Valor = 15m, Descricao = "Pedido Z", IdempotencyKey = "cardapio:z"
        }));
        Assert.Equal(503, semResposta.Http);
        Assert.Equal("criando", await db.Scalar<string>("SELECT status FROM pagamento_tentativa"));

        await db.Conciliador().RodarAsync(CancellationToken.None);
        Assert.Equal(1L, await db.Scalar<long>("SELECT count(*) FROM pagamento"));
        Assert.Equal("criada", await db.Scalar<string>("SELECT status FROM pagamento_tentativa"));
        Assert.Equal(StatusIntent.AguardandoPagamento, await db.Scalar<string>("SELECT status FROM pagamento_intent"));

        // Repetir a criacao depois disso nao gera segundo Pix no MP.
        var r = await pix.CriarAsync(new PixChargeRequest
        {
            IdEstabelecimento = db.Loja, Origem = OrigensPagamento.Cardapio, Valor = 15m, Descricao = "Pedido Z", IdempotencyKey = "cardapio:z"
        });
        Assert.True(r.Reaproveitado);
        Assert.Equal(1, db.Mp.Pagamentos.Count);

        // Pagamento que volta com outro dono (collector) e recusado: nada e gravado.
        db.Mp.CollectorForcado = 999;
        var errado = await Assert.ThrowsAsync<PagamentoException>(() => pix.CriarAsync(new PixChargeRequest
        {
            IdEstabelecimento = db.Loja, Origem = OrigensPagamento.Cardapio, Valor = 16m, Descricao = "Pedido Y", IdempotencyKey = "cardapio:y"
        }));
        Assert.Equal(PagamentoCodigos.MercadoPagoRespostaInconsistente, errado.Codigo);
        Assert.Equal(1L, await db.Scalar<long>("SELECT count(*) FROM pagamento"));

        // Mensalidade (conta Zippy) sem conta Zippy configurada: 503, nunca cai na conta da loja.
        var semZippy = await Assert.ThrowsAsync<PagamentoException>(() => pix.CriarAsync(new PixChargeRequest
        {
            IdEstabelecimento = db.Loja, Origem = OrigensPagamento.Assinatura, Valor = 149m, Descricao = "Plano", IdempotencyKey = "assinatura:1"
        }));
        Assert.Equal(PagamentoCodigos.NaoConfigurado, semZippy.Codigo);
    }

    // ------------------------------------------------------------------------------------------------------------------------

    private sealed class Banco : IAsyncDisposable
    {
        public Guid Loja { get; } = Guid.NewGuid();
        public Guid Conversa { get; } = Guid.NewGuid();
        public Guid PrePedido { get; } = Guid.NewGuid();
        public MpFalso Mp { get; } = new();
        private ServiceProvider _sp = null!;
        private NpgsqlDataSource _source = null!;
        private readonly string _schema = "mp_nucleo_" + Guid.NewGuid().ToString("N");
        private string _base = "";

        public T Get<T>() where T : notnull => _sp.CreateScope().ServiceProvider.GetRequiredService<T>();

        public PagamentoConciliadorWorker Conciliador() => ActivatorUtilities.CreateInstance<PagamentoConciliadorWorker>(_sp);

        public static async Task<Banco> Criar()
        {
            APIBack.Infrastructure.DapperConfiguration.Configure();
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE"));
            if (options.Host is not ("localhost" or "127.0.0.1")) throw new InvalidOperationException("Use somente PostgreSQL local.");
            var db = new Banco { _base = options.ConnectionString };
            await using (var cx = new NpgsqlConnection(db._base)) { await cx.OpenAsync(); await cx.ExecuteAsync($"CREATE SCHEMA {db._schema}"); }
            options.SearchPath = db._schema;
            db._source = NpgsqlDataSource.Create(options.ConnectionString);

            await db.Execute(@"
CREATE TABLE estabelecimentos (id uuid PRIMARY KEY, nome_fantasia text);
CREATE TABLE conversas (id uuid PRIMARY KEY);
CREATE TABLE clientes (id uuid PRIMARY KEY);
CREATE TABLE cardapio_pedido_publico (id uuid PRIMARY KEY, id_estabelecimento uuid, status_pagamento text NOT NULL DEFAULT 'pendente', updated_at timestamptz);
CREATE TABLE cardapio_web_config (id_estabelecimento uuid PRIMARY KEY, nome_publico text);
CREATE TABLE delivery_tracking_schema_versions (version text PRIMARY KEY);
INSERT INTO estabelecimentos VALUES (@L, 'Loja Teste');
INSERT INTO conversas VALUES (@C);
INSERT INTO cardapio_pedido_publico (id, id_estabelecimento) VALUES (@P, @L);", new { L = db.Loja, C = db.Conversa, P = db.PrePedido });
            await db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261031_01_mercadopago_nucleo.sql")));
            await db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261102_60_mercadopago_orders.sql")));

            var chave = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payments:ChaveMestra"] = chave,
                ["Payments:MercadoPago:Ambiente"] = "sandbox",
                ["Payments:MercadoPago:NotificationUrl"] = "https://api.teste/api/webhooks/mercadopago",
                ["Payments:MercadoPago:ConciliadorIdadeMinimaSegundos"] = "0"
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton<IConfiguration>(config);
            services.AddSingleton(db._source);
            services.AddPagamentosMercadoPago(config);
            services.AddHttpClient(MercadoPagoClient.NomeHttpClient).ConfigurePrimaryHttpMessageHandler(() => db.Mp);
            db._sp = services.BuildServiceProvider();

            var protector = db._sp.GetRequiredService<IPagamentoSegredoProtector>();
            var app = Guid.NewGuid();
            var conta = Guid.NewGuid();
            await db.Execute(@"
INSERT INTO mp_aplicacao (id, ambiente, client_id, client_secret_cifrado, webhook_secret_cifrado) VALUES (@App, 'sandbox', '123', @Cs, @Ws);
INSERT INTO mp_conta_loja (id, id_estabelecimento, ambiente, mp_user_id, mp_user_id_mascarado, access_token_cifrado, refresh_token_cifrado, public_key, token_expira_em)
VALUES (@Conta, @L, 'sandbox', @U, '**5001', @At, @Rt, 'TEST-pk', NOW() + INTERVAL '150 days');
INSERT INTO pagamento_preferencia_loja (id_estabelecimento, pix_ativo, cartao_ativo) VALUES (@L, TRUE, TRUE);", new
            {
                App = app, Conta = conta, L = db.Loja, U = MpUserLoja,
                Cs = protector.Proteger("client-secret", SegredoContexto.AplicacaoClientSecret(app)),
                Ws = protector.Proteger(WebhookSecret, SegredoContexto.AplicacaoWebhookSecret(app)),
                At = protector.Proteger("TOKEN-DA-LOJA", SegredoContexto.LojaAccessToken(conta)),
                Rt = protector.Proteger("REFRESH-DA-LOJA", SegredoContexto.LojaRefreshToken(conta))
            });
            db.Mp.Collector = MpUserLoja;
            return db;
        }

        public MpWebhookEntrada Aviso(long mpId, string notificacao)
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var req = Guid.NewGuid().ToString();
            var v1 = MercadoPagoAssinatura.Assinar(WebhookSecret, MercadoPagoAssinatura.Manifesto(mpId.ToString(), req, ts));
            return new MpWebhookEntrada
            {
                Assinatura = $"ts={ts},v1={v1}", RequestId = req, QueryDataId = mpId.ToString(), QueryTipo = "payment",
                CorpoBruto = JsonSerializer.Serialize(new { id = notificacao, type = "payment", action = "payment.updated", live_mode = false, user_id = MpUserLoja, data = new { id = mpId.ToString() } })
            };
        }

        public async Task Execute(string sql, object? args = null) { await using var cx = await _source.OpenConnectionAsync(); await cx.ExecuteAsync(sql, args); }
        public async Task<T> Scalar<T>(string sql) { await using var cx = await _source.OpenConnectionAsync(); return (await cx.ExecuteScalarAsync<T>(sql))!; }

        public async ValueTask DisposeAsync()
        {
            await _sp.DisposeAsync();
            await _source.DisposeAsync();
            await using var cx = new NpgsqlConnection(_base); await cx.OpenAsync(); await cx.ExecuteAsync($"DROP SCHEMA {_schema} CASCADE");
        }
    }

    /// <summary>MP falso: guarda pagamentos, respeita X-Idempotency-Key e devolve o formato da API v1.</summary>
    private sealed class MpFalso : HttpMessageHandler
    {
        public sealed record Criacao(string? Authorization, string? Idempotencia, JsonObject Corpo);

        private long _proximo = 9_000_000_000;
        private readonly ConcurrentDictionary<string, long> _porChave = new();
        public ConcurrentDictionary<long, MpPagamento> Pagamentos { get; } = new();
        public List<Criacao> Criacoes { get; } = new();
        public List<(long Pagamento, decimal? Valor)> Estornos { get; } = new();
        public long Collector { get; set; }
        public long? CollectorForcado { get; set; }
        public bool PerderProximaResposta { get; set; }

        public void Mudar(long id, string status, string detalhe, DateTimeOffset quando)
        {
            var p = Pagamentos[id];
            p.Status = status;
            p.StatusDetail = detalhe;
            p.DateLastUpdated = quando;
            if (status == "approved") p.DateApproved = quando;
        }

        public MpPagamento Copia(long id) => JsonSerializer.Deserialize<MpPagamento>(JsonSerializer.Serialize(Pagamentos[id]))!;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var caminho = request.RequestUri!.AbsolutePath.TrimStart('/');
            var corpo = request.Content == null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct))?.AsObject();
            var chave = request.Headers.TryGetValues("X-Idempotency-Key", out var v) ? v.First() : null;

            if (request.Method == HttpMethod.Post && caminho == "v1/orders")
            {
                lock (Criacoes) Criacoes.Add(new Criacao(request.Headers.Authorization?.ToString(), chave, corpo!));
                var id = _porChave.GetOrAdd(chave!, _ =>
                {
                    var novo = Interlocked.Increment(ref _proximo);
                    Pagamentos[novo] = new MpPagamento
                    {
                        Id = novo, Status = "pending", StatusDetail = "pending_waiting_transfer", CollectorId = CollectorForcado ?? Collector,
                        TransactionAmount = MpOrdersFalso.Valor(corpo!), CurrencyId = "BRL", LiveMode = false,
                        ExternalReference = MpOrdersFalso.Referencia(corpo!), PaymentMethodId = MpOrdersFalso.Metodo(corpo!)["id"]!.GetValue<string>(),
                        PaymentTypeId = "bank_transfer", DateCreated = DateTimeOffset.UtcNow, DateLastUpdated = DateTimeOffset.UtcNow,
                        PointOfInteraction = new MpPointOfInteraction { TransactionData = new MpTransactionData { QrCode = "00020126pix" + novo, QrCodeBase64 = "iVBOR", TicketUrl = "https://mp/t" } }
                    };
                    return novo;
                });
                if (PerderProximaResposta) { PerderProximaResposta = false; throw new HttpRequestException("conexao caiu"); }
                return MpOrdersFalso.Resposta(Pagamentos[id]);
            }

            var partes = caminho.Split('/');
            if (partes.Length >= 3 && partes[1] == "orders" && MpOrdersFalso.TentaPagamento(partes[2], out var pid) && Pagamentos.TryGetValue(pid, out var pag))
            {
                if (request.Method == HttpMethod.Get && partes.Length == 3) return MpOrdersFalso.Resposta(pag);
                if (request.Method == HttpMethod.Post && partes.Length == 4 && partes[3] == "cancel")
                {
                    Mudar(pid, "cancelled", "by_collector", DateTimeOffset.UtcNow);
                    return MpOrdersFalso.Resposta(pag);
                }
                if (request.Method == HttpMethod.Post && partes.Length == 4 && partes[3] == "refund")
                {
                    var valor = MpOrdersFalso.ValorEstorno(corpo);
                    var jaEstornado = (pag.Refunds ?? new()).Sum(x => x.Amount);
                    var refund = new MpRefund { Id = Interlocked.Increment(ref _proximo), Amount = valor ?? pag.TransactionAmount - jaEstornado, Status = "approved" };
                    lock (Estornos) Estornos.Add((pid, valor));
                    pag.Refunds = (pag.Refunds ?? new()).Append(refund).ToList();
                    pag.TransactionAmountRefunded = pag.Refunds.Sum(x => x.Amount);
                    if (pag.TransactionAmountRefunded >= pag.TransactionAmount) pag.Status = "refunded";
                    pag.DateLastUpdated = DateTimeOffset.UtcNow;
                    return MpOrdersFalso.Resposta(pag);
                }
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"not_found\"}") };
        }

        private static HttpResponseMessage Json(object o) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
    }
}
