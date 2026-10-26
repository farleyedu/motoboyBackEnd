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
using APIBack.Automation.Interfaces;
using APIBack.DTOs.Delivery;
using APIBack.Model.Cardapio;
using APIBack.Payments.Controllers;
using APIBack.Payments.MercadoPago;
using APIBack.Repository;
using APIBack.Repository.Interface;
using APIBack.Service;
using APIBack.Service.Interface;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// Pagamento online do cardapio web contra PostgreSQL real (TEST_DELIVERY_DATABASE), com as migrations do cardapio, do nucleo Mercado Pago e
/// 20261101_30, e um Mercado Pago falso. Prova: valor do servidor e idempotencia (um pre-pedido, um Pix), pagamento confirmado so pelo
/// webhook libera o aceite, recusa de pedido pago estorna integral uma vez, aceite e cancelamento disputando o mesmo pedido (so um vence),
/// pagamento que chega depois do cancelamento volta e o pago sem confirmacao do numero e devolvido.
/// </summary>
public sealed class CardapioPagamentoDatabaseTests
{
    private const string WebhookSecret = "whsec-cardapio";
    private const long MpUserLoja = 777001;

    [DeliveryDatabaseFact]
    public async Task PixUsaOValorDoServidorEhIdempotenteESoOWebhookLiberaOAceite()
    {
        await using var db = await Banco.Criar();
        var pre = await db.PrePedido(42.90m, CardapioPedidoStatus.AguardandoPagamento);
        var pagamentos = db.Get<ICardapioPagamentoService>();

        var pix1 = await pagamentos.CriarPixAsync(pre, "CDP-TESTE");
        var pix2 = await pagamentos.CriarPixAsync(pre, "cdp-teste");

        Assert.Equal(pix1.CopiaECola, pix2.CopiaECola);
        var criacao = Assert.Single(db.Mp.Criacoes);
        Assert.Equal(42.90m, MpOrdersFalso.Valor(criacao.Corpo));
        Assert.Equal(42.90m, pix1.Valor);
        // Codigo errado = 404 (nao revela o pedido).
        Assert.Equal(404, (await Assert.ThrowsAsync<PagamentoException>(() => pagamentos.CriarPixAsync(pre, "CDP-OUTRO"))).Http);

        // O "voltei do banco" do navegador nao muda nada: so o aviso do Mercado Pago.
        Assert.Equal(CardapioPedidoStatus.AguardandoPagamento, await db.Status(pre));
        Assert.Empty(await db.Get<ICardapioPedidoWebRepository>().ListarAguardandoAceiteAsync(db.Loja, 10));

        await db.Pagar(pix1Id: await db.UltimoMp());
        Assert.Equal(CardapioPedidoStatus.AguardandoAceite, await db.Status(pre));
        Assert.Equal("pago", await db.Scalar<string>($"SELECT status_pagamento FROM cardapio_pedido_publico WHERE id = '{pre}'"));
        Assert.Single(await db.Get<ICardapioPedidoWebRepository>().ListarAguardandoAceiteAsync(db.Loja, 10));

        // Pedido pago nao gera outro Pix nem cartao.
        Assert.Equal(PagamentoCodigos.JaPago, (await Assert.ThrowsAsync<PagamentoException>(() => pagamentos.CriarPixAsync(pre, "CDP-TESTE"))).Codigo);
        Assert.Single(db.Mp.Criacoes);

        // Presencial nunca e cobrado online.
        var presencial = await db.PrePedido(30m, CardapioPedidoStatus.AguardandoAceite, online: false);
        Assert.Equal(PagamentoCodigos.PedidoNaoPagavel,
            (await Assert.ThrowsAsync<PagamentoException>(() => pagamentos.CriarPixAsync(presencial, "CDP-TESTE"))).Codigo);
    }

    [DeliveryDatabaseFact]
    public async Task RecusaDoPedidoPagoEstornaIntegralUmaVezSo()
    {
        await using var db = await Banco.Criar();
        var pre = await db.PrePedido(55m, CardapioPedidoStatus.AguardandoPagamento);
        await db.Get<ICardapioPagamentoService>().CriarPixAsync(pre, "CDP-TESTE");
        await db.Pagar(await db.UltimoMp());

        var web = db.Web();
        await web.RecusarAsync(db.Loja, pre, "acabou o queijo");
        await web.RecusarAsync(db.Loja, pre, "acabou o queijo");

        Assert.Equal(CardapioPedidoStatus.Recusado, await db.Status(pre));
        var estorno = Assert.Single(db.Mp.Estornos);
        Assert.Null(estorno.Valor); // integral
        Assert.Equal(1L, await db.Scalar<long>("SELECT count(*) FROM cardapio_pedido_reembolso"));
        Assert.Equal(CardapioReembolsoStatus.Reembolsado, await db.Scalar<string>("SELECT status FROM cardapio_pedido_reembolso"));
        Assert.Equal(StatusIntent.Estornado, await db.Scalar<string>("SELECT status FROM pagamento_intent"));
        var resumo = await db.Get<ICardapioPagamentoService>().ResumoAsync((await db.Get<ICardapioPedidoWebRepository>().ObterAsync(pre))!);
        Assert.Equal("reembolsado", resumo!.Status);
        Assert.Equal(55m, resumo.ValorEstornado);
        // O trabalhador rodando depois nao estorna de novo.
        await db.Get<ICardapioPagamentoService>().ProcessarReembolsosAsync();
        Assert.Single(db.Mp.Estornos);
    }

    [DeliveryDatabaseFact]
    public async Task AceiteECancelamentoDisputandoOMesmoPedidoSoUmVence()
    {
        await using var db = await Banco.Criar();
        for (var rodada = 0; rodada < 6; rodada++)
        {
            db.Mp.Estornos.Clear();
            var pre = await db.PrePedido(20m + rodada, CardapioPedidoStatus.AguardandoPagamento);
            await db.Get<ICardapioPagamentoService>().CriarPixAsync(pre, "CDP-TESTE");
            await db.Pagar(await db.UltimoMp());

            var core = new Mock<IPedidoCoreService>();
            var chamadasCore = 0;
            core.Setup(c => c.CreateAsync(db.Loja, It.IsAny<int>(), It.IsAny<CreatePedidoRequest>(), null, true))
                .Returns(async () => { Interlocked.Increment(ref chamadasCore); await Task.Delay(30); return new CreatedPedidoDto { Id = 900 + rodada, Status = "pendente", Total = 20m }; });

            var largada = new TaskCompletionSource();
            var aceitar = Task.Run(async () => { await largada.Task; return await db.Web(core.Object).AceitarAsync(db.Loja, 9, pre); });
            var cancelar = Task.Run(async () => { await largada.Task; await db.Get<ICardapioPagamentoService>().CancelarPeloClienteAsync(pre, "CDP-TESTE", "desisti"); });
            largada.SetResult();
            var aceitou = await Termina(aceitar);
            var cancelou = await Termina(cancelar);

            var status = await db.Status(pre);
            var reembolsos = await db.Scalar<long>($"SELECT count(*) FROM cardapio_pedido_reembolso WHERE pre_pedido_id = '{pre}'");
            Assert.True(aceitou ^ cancelou, $"rodada {rodada}: aceitou={aceitou} cancelou={cancelou}");
            if (aceitou)
            {
                Assert.Equal(CardapioPedidoStatus.Aceito, status);
                Assert.Equal(0L, reembolsos);
                Assert.Empty(db.Mp.Estornos);
            }
            else
            {
                Assert.Equal(CardapioPedidoStatus.Cancelado, status);
                Assert.Equal(1L, reembolsos);
                Assert.Single(db.Mp.Estornos);
                Assert.Equal(0, chamadasCore);
            }
        }
    }

    [DeliveryDatabaseFact]
    public async Task PagamentoQueChegaDepoisDoCancelamentoVoltaEOAbandonadoEDevolvido()
    {
        await using var db = await Banco.Criar();
        var pagamentos = db.Get<ICardapioPagamentoService>();

        // Cancelado antes de pagar: so a intencao e cancelada, nenhum estorno.
        var pre = await db.PrePedido(31m, CardapioPedidoStatus.AguardandoPagamento);
        await pagamentos.CriarPixAsync(pre, "CDP-TESTE");
        var mpId = await db.UltimoMp();
        await pagamentos.CancelarPeloClienteAsync(pre, "CDP-TESTE", null);
        Assert.Equal(CardapioPedidoStatus.Cancelado, await db.Status(pre));
        Assert.Equal(StatusIntent.Cancelado, await db.Scalar<string>($"SELECT status FROM pagamento_intent WHERE pre_pedido_id = '{pre}'"));
        Assert.Empty(db.Mp.Estornos);

        // O banco liquidou o Pix mesmo assim: o efeito do webhook agenda o reembolso e o trabalhador devolve.
        await db.Pagar(mpId);
        Assert.Equal(CardapioReembolsoMotivo.PagoAposEncerramento,
            await db.Scalar<string>($"SELECT motivo FROM cardapio_pedido_reembolso WHERE pre_pedido_id = '{pre}'"));
        await pagamentos.ProcessarReembolsosAsync();
        Assert.Single(db.Mp.Estornos);
        Assert.Equal(CardapioPedidoStatus.Cancelado, await db.Status(pre));

        // Pagou e nunca mandou o codigo: passado o prazo o dinheiro volta sozinho.
        var abandonado = await db.PrePedido(18m, CardapioPedidoStatus.AguardandoCodigo);
        await pagamentos.CriarPixAsync(abandonado, "CDP-TESTE");
        await db.Pagar(await db.UltimoMp());
        Assert.Equal(CardapioPedidoStatus.AguardandoCodigo, await db.Status(abandonado));
        await db.Execute($"UPDATE cardapio_pedido_publico SET pago_em = NOW() - INTERVAL '2 hours' WHERE id = '{abandonado}'");
        Assert.Equal(1, await pagamentos.DevolverAbandonadosAsync());
        Assert.Equal(CardapioPedidoStatus.Cancelado, await db.Status(abandonado));
        Assert.Equal(2, db.Mp.Estornos.Count);
        Assert.Equal(CardapioReembolsoStatus.Reembolsado,
            await db.Scalar<string>($"SELECT status FROM cardapio_pedido_reembolso WHERE pre_pedido_id = '{abandonado}'"));
    }

    private static async Task<bool> Termina(Task tarefa)
    {
        try
        {
            await tarefa;
            return true;
        }
        catch (Exception ex) when (ex is DeliveryDomainException or PagamentoException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------

    private sealed class Banco : IAsyncDisposable
    {
        public Guid Loja { get; } = Guid.NewGuid();
        public MpFalso Mp { get; } = new();
        private ServiceProvider _sp = null!;
        private NpgsqlDataSource _source = null!;
        private readonly string _schema = "cardapio_pag_" + Guid.NewGuid().ToString("N");
        private string _base = "";

        public T Get<T>() where T : notnull => _sp.CreateScope().ServiceProvider.GetRequiredService<T>();

        public CardapioPedidoWebService Web(IPedidoCoreService? core = null)
        {
            var cardapio = new Mock<ICardapioRepository>();
            cardapio.Setup(c => c.ObterEstabelecimentoPublicoAsync(It.IsAny<Guid?>(), It.IsAny<string?>()))
                .ReturnsAsync(new CardapioEstabelecimentoPublico { Id = Loja, NomeFantasia = "Loja Teste" });
            var rastreio = new Mock<IRastreioRepository>();
            rastreio.Setup(r => r.GetSettingsAsync(It.IsAny<Guid>())).ReturnsAsync(new APIBack.Service.NoticeSettings());
            return new CardapioPedidoWebService(Get<ICardapioPedidoWebRepository>(), cardapio.Object, Mock.Of<IConversationRepository>(),
                Mock.Of<IClienteRepository>(), Mock.Of<IClienteCadastroRepository>(), Mock.Of<IWabaPhoneRepository>(), Mock.Of<ITrackingNoticeSender>(),
                core ?? Mock.Of<IPedidoCoreService>(), rastreio.Object, new MemoryCache(new MemoryCacheOptions()),
                NullLogger<CardapioPedidoWebService>.Instance, null, Get<ICardapioPagamentoService>());
        }

        public static async Task<Banco> Criar()
        {
            APIBack.Infrastructure.DapperConfiguration.Configure();
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE"));
            if (options.Host is not ("localhost" or "127.0.0.1")) throw new InvalidOperationException("Use somente PostgreSQL local.");
            var db = new Banco { _base = options.ConnectionString };
            await using (var cx = new NpgsqlConnection(db._base)) { await cx.OpenAsync(); await cx.ExecuteAsync($"CREATE SCHEMA {db._schema}"); }
            options.SearchPath = db._schema;
            db._source = NpgsqlDataSource.Create(options.ConnectionString);

            // Pre-pedido como em 20261001_01 + 20261002_03; o resto vem das migrations reais.
            await db.Execute(@"
CREATE TABLE estabelecimentos (id uuid PRIMARY KEY, nome_fantasia text);
CREATE TABLE conversas (id uuid PRIMARY KEY);
CREATE TABLE clientes (id uuid PRIMARY KEY);
CREATE TABLE cardapio_web_config (id_estabelecimento uuid PRIMARY KEY, nome_publico text);
CREATE TABLE delivery_tracking_schema_versions (version text PRIMARY KEY);
CREATE TABLE pedido (id serial PRIMARY KEY, id_estabelecimento uuid, status_pedido int);
CREATE TABLE cardapio_pedido_publico (
    id uuid PRIMARY KEY, id_estabelecimento uuid NOT NULL REFERENCES estabelecimentos (id), codigo text NOT NULL, status text NOT NULL DEFAULT 'pendente',
    tipo_entrega text NOT NULL DEFAULT 'retirada', nome_cliente text NOT NULL, telefone_cliente text NOT NULL, email_cliente text, forma_pagamento text,
    observacoes text, subtotal_produtos numeric(10,2) NOT NULL DEFAULT 0, subtotal_adicionais numeric(10,2) NOT NULL DEFAULT 0,
    taxa_entrega numeric(10,2) NOT NULL DEFAULT 0, total numeric(10,2) NOT NULL DEFAULT 0, itens_json jsonb NOT NULL DEFAULT '[]', endereco_entrega_json jsonb,
    status_pagamento text NOT NULL DEFAULT 'pendente', created_at timestamptz NOT NULL DEFAULT NOW(), updated_at timestamptz NOT NULL DEFAULT NOW(),
    codigo_confirmacao text, codigo_expira_em timestamptz, canal_confirmacao text, telefone_contato text, id_conversa uuid, confirmado_em timestamptz,
    aceito_em timestamptz, recusado_em timestamptz, motivo_recusa text, id_pedido int);
INSERT INTO estabelecimentos VALUES (@L, 'Loja Teste');", new { L = db.Loja });
            foreach (var migration in new[] { "20261031_01_mercadopago_nucleo.sql", "20261102_60_mercadopago_orders.sql", "20261101_30_cardapio_pagamento_online.sql" })
                await db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", migration)));

            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payments:ChaveMestra"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["Payments:MercadoPago:Ambiente"] = "sandbox",
                ["Payments:MercadoPago:NotificationUrl"] = "https://api.teste/api/webhooks/mercadopago"
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton<IConfiguration>(config);
            services.AddSingleton(db._source);
            services.AddPagamentosMercadoPago(config);
            services.AddHttpClient(MercadoPagoClient.NomeHttpClient).ConfigurePrimaryHttpMessageHandler(() => db.Mp);
            services.AddScoped<ICardapioPedidoWebRepository, CardapioPedidoWebRepository>();
            services.AddScoped<ICardapioPagamentoService, CardapioPagamentoService>();
            db._sp = services.BuildServiceProvider();

            var protector = db._sp.GetRequiredService<IPagamentoSegredoProtector>();
            var app = Guid.NewGuid();
            var conta = Guid.NewGuid();
            await db.Execute(@"
INSERT INTO mp_aplicacao (id, ambiente, client_id, client_secret_cifrado, webhook_secret_cifrado) VALUES (@App, 'sandbox', '123', @Cs, @Ws);
INSERT INTO mp_conta_loja (id, id_estabelecimento, ambiente, mp_user_id, mp_user_id_mascarado, access_token_cifrado, refresh_token_cifrado, public_key, token_expira_em)
VALUES (@Conta, @L, 'sandbox', @U, '**7001', @At, @Rt, 'TEST-pk', NOW() + INTERVAL '150 days');
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

        public async Task<Guid> PrePedido(decimal total, string status, bool online = true)
        {
            var id = Guid.NewGuid();
            await Execute(@"
INSERT INTO cardapio_pedido_publico (id, id_estabelecimento, codigo, status, nome_cliente, telefone_cliente, forma_pagamento, total, pagamento_online, metodo_online)
VALUES (@Id, @L, 'CDP-TESTE' || substr(@Id::text, 1, 0), @S, 'Maria', '+5534991230001', @F, @T, @O, @M)",
                new { Id = id, L = Loja, S = status, T = total, O = online, M = online ? "pix" : null, F = online ? "pix_online" : "Dinheiro" });
            // Codigo do pedido unico por linha (o indice exige), mas o teste usa sempre CDP-TESTE como segundo fator.
            await Execute("DROP INDEX IF EXISTS ux_cardapio_pedido_publico_codigo");
            return id;
        }

        public Task<long> UltimoMp() => Scalar<long>("SELECT mp_payment_id FROM pagamento ORDER BY created_at DESC LIMIT 1");

        /// <summary>O Mercado Pago aprova e avisa pelo webhook assinado (unico caminho que confirma pagamento).</summary>
        public async Task Pagar(long pix1Id)
        {
            Mp.Mudar(pix1Id, "approved", "accredited", DateTimeOffset.UtcNow);
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var req = Guid.NewGuid().ToString();
            var v1 = MercadoPagoAssinatura.Assinar(WebhookSecret, MercadoPagoAssinatura.Manifesto(pix1Id.ToString(), req, ts));
            var r = await Get<IMercadoPagoWebhookService>().ReceberAsync(new MpWebhookEntrada
            {
                Assinatura = $"ts={ts},v1={v1}", RequestId = req, QueryDataId = pix1Id.ToString(), QueryTipo = "payment",
                CorpoBruto = JsonSerializer.Serialize(new { id = Guid.NewGuid().ToString(), type = "payment", action = "payment.updated", live_mode = false, user_id = MpUserLoja, data = new { id = pix1Id.ToString() } })
            });
            Assert.Equal("processado", r.StatusProcessamento);
        }

        public Task<string> Status(Guid pre) => Scalar<string>($"SELECT status FROM cardapio_pedido_publico WHERE id = '{pre}'");

        public async Task Execute(string sql, object? args = null) { await using var cx = await _source.OpenConnectionAsync(); await cx.ExecuteAsync(sql, args); }
        public async Task<T> Scalar<T>(string sql) { await using var cx = await _source.OpenConnectionAsync(); return (await cx.ExecuteScalarAsync<T>(sql))!; }

        public async ValueTask DisposeAsync()
        {
            await _sp.DisposeAsync();
            await _source.DisposeAsync();
            await using var cx = new NpgsqlConnection(_base); await cx.OpenAsync(); await cx.ExecuteAsync($"DROP SCHEMA {_schema} CASCADE");
        }
    }

    /// <summary>MP falso (mesmo contrato da API v1): guarda pagamentos, respeita X-Idempotency-Key, cancela e estorna.</summary>
    private sealed class MpFalso : HttpMessageHandler
    {
        public sealed record Criacao(string? Idempotencia, JsonObject Corpo);

        private long _proximo = 8_000_000_000;
        private readonly ConcurrentDictionary<string, long> _porChave = new();
        public ConcurrentDictionary<long, MpPagamento> Pagamentos { get; } = new();
        public List<Criacao> Criacoes { get; } = new();
        public List<(long Pagamento, decimal? Valor)> Estornos { get; } = new();
        public long Collector { get; set; }

        public void Mudar(long id, string status, string detalhe, DateTimeOffset quando)
        {
            var p = Pagamentos[id];
            p.Status = status;
            p.StatusDetail = detalhe;
            p.DateLastUpdated = quando;
            if (status == "approved") p.DateApproved = quando;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var caminho = request.RequestUri!.AbsolutePath.TrimStart('/');
            var corpo = request.Content == null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct))?.AsObject();
            var chave = request.Headers.TryGetValues("X-Idempotency-Key", out var v) ? v.First() : null;

            if (request.Method == HttpMethod.Post && caminho == "v1/orders")
            {
                lock (Criacoes) Criacoes.Add(new Criacao(chave, corpo!));
                var id = _porChave.GetOrAdd(chave!, _ =>
                {
                    var novo = Interlocked.Increment(ref _proximo);
                    Pagamentos[novo] = new MpPagamento
                    {
                        Id = novo, Status = "pending", StatusDetail = "pending_waiting_transfer", CollectorId = Collector,
                        TransactionAmount = MpOrdersFalso.Valor(corpo!), CurrencyId = "BRL", LiveMode = false,
                        ExternalReference = MpOrdersFalso.Referencia(corpo!), PaymentMethodId = MpOrdersFalso.Metodo(corpo!)["id"]!.GetValue<string>(),
                        PaymentTypeId = "bank_transfer", DateCreated = DateTimeOffset.UtcNow, DateLastUpdated = DateTimeOffset.UtcNow,
                        PointOfInteraction = new MpPointOfInteraction { TransactionData = new MpTransactionData { QrCode = "00020126pix" + novo, QrCodeBase64 = "iVBOR", TicketUrl = "https://mp/t" } }
                    };
                    return novo;
                });
                return MpOrdersFalso.Resposta(Pagamentos[id]);
            }

            var partes = caminho.Split('/');
            if (partes.Length >= 3 && partes[1] == "orders" && MpOrdersFalso.TentaPagamento(partes[2], out var pid) && Pagamentos.TryGetValue(pid, out var pag))
            {
                if (request.Method == HttpMethod.Get && partes.Length == 3) return MpOrdersFalso.Resposta(pag);
                if (request.Method == HttpMethod.Post && partes.Length == 4 && partes[3] == "cancel")
                {
                    if (pag.Status == "pending") Mudar(pid, "cancelled", "by_collector", DateTimeOffset.UtcNow);
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
                    pag.DateLastUpdated = DateTimeOffset.UtcNow.AddMilliseconds(1);
                    return MpOrdersFalso.Resposta(pag);
                }
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"not_found\"}") };
        }

        private static HttpResponseMessage Json(object o) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
    }
}
