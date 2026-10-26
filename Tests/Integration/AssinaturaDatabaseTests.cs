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
using APIBack.Payments.Assinaturas;
using APIBack.Payments.MercadoPago;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// Assinaturas contra PostgreSQL real (TEST_DELIVERY_DATABASE) e um MP falso. Prova: contratar e pagar sao idempotentes (duplo clique),
/// o Pix cai na conta da ZIPPY, o pagamento ativa o plano uma vez so, Pix tardio vira excedente, a inadimplencia passa por
/// atraso -> suspensa -> volta sozinha ao pagar, upgrade cobra o proporcional, downgrade espera o fim do ciclo, cancelamento mantem o acesso.
/// </summary>
public sealed class AssinaturaDatabaseTests
{
    private const long MpUserZippy = 777001;

    [DeliveryDatabaseFact]
    public async Task ContratarPagarComPixIdempotenteAtivaOPlano()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        var svc = db.Get<IAssinaturaService>();

        var a1 = await svc.ContratarAsync(db.Loja, new ContratarRequest { Plano = "rota", Periodicidade = "mensal", IdempotencyKey = "k1" }, 7);
        var a2 = await svc.ContratarAsync(db.Loja, new ContratarRequest { Plano = "rota", Periodicidade = "mensal", IdempotencyKey = "k1" }, 7);
        Assert.Equal(a1.Id, a2.Id);
        var outra = await Assert.ThrowsAsync<PagamentoException>(() => svc.ContratarAsync(db.Loja, new ContratarRequest { Plano = "turbo", IdempotencyKey = "k2" }, 7));
        Assert.Equal(AssinaturaCodigos.JaExiste, outra.Codigo);
        Assert.Equal(1, await db.Scalar<long>("SELECT count(*) FROM assinatura"));
        Assert.Equal(249m, a1.Preco); // preco do catalogo, nunca do navegador
        var cobranca = Assert.Single(await svc.CobrancasAsync(db.Loja));
        Assert.Equal(249m, cobranca.Valor);

        // Duplo clique em "Pagar com Pix": a mesma intencao, uma chamada ao MP, na conta da Zippy.
        var p1 = await svc.PagarAsync(db.Loja, cobranca.Id, new PagarCobrancaRequest { Metodo = "pix" }, 7);
        var p2 = await svc.PagarAsync(db.Loja, cobranca.Id, new PagarCobrancaRequest { Metodo = "pix" }, 7);
        Assert.Equal(p1.Pix!.IntentId, p2.Pix!.IntentId);
        Assert.True(p2.Pix.Reaproveitado);
        var criacao = Assert.Single(db.Mp.Criacoes);
        Assert.Equal("Bearer TOKEN-ZIPPY", criacao.Authorization);
        Assert.Equal("zippy", await db.Scalar<string>("SELECT conta_tipo FROM pagamento_intent"));
        Assert.Equal(cobranca.Id, await db.Scalar<Guid>("SELECT origem_ref_id FROM pagamento_intent"));

        // Aprovado (webhook/conciliador): ativa uma vez so, mesmo com o aviso repetido.
        var mpId = db.Mp.Pagamentos.Keys.Single();
        db.Mp.Aprovar(mpId);
        var sync = db.Get<IPagamentoSincronizador>();
        var cred = await db.Get<IMercadoPagoCredencialResolver>().RecebedorZippyAsync();
        await sync.SincronizarAsync(cred, mpId, "webhook");
        await sync.SincronizarAsync(cred, mpId, "webhook");
        Assert.Equal("paga", await db.Scalar<string>("SELECT status FROM comercial_cobranca"));
        Assert.Equal("ativa", await db.Scalar<string>("SELECT status FROM assinatura"));
        Assert.Equal("rota", await db.Scalar<string>("SELECT plano_comercial FROM estabelecimentos"));
        Assert.Equal(1, await db.Scalar<long>("SELECT count(*) FROM estabelecimento_situacao_log WHERE motivo = 'assinatura'"));
        Assert.Equal(1, await db.Scalar<long>("SELECT count(*) FROM pagamento_auditoria WHERE entidade = 'comercial_cobranca' AND acao = 'paga'"));

        // Pagar de novo o que ja foi pago: 409 sem nova divida.
        var dup = await Assert.ThrowsAsync<PagamentoException>(() => svc.PagarAsync(db.Loja, cobranca.Id, new PagarCobrancaRequest { Metodo = "pix" }, 7));
        Assert.Equal(PagamentoCodigos.JaPago, dup.Codigo);
        Assert.Equal(1, await db.Scalar<long>("SELECT count(*) FROM comercial_cobranca"));
    }

    [DeliveryDatabaseFact]
    public async Task PixTardioDepoisDaBaixaManualViraExcedenteSemContarDuasVezes()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        var svc = db.Get<IAssinaturaService>();
        await svc.ContratarAsync(db.Loja, new ContratarRequest { Plano = "rota", IdempotencyKey = "k" }, 7);
        var c = (await svc.CobrancasAsync(db.Loja)).Single();
        await svc.PagarAsync(db.Loja, c.Id, new PagarCobrancaRequest { Metodo = "pix" }, 7);
        var mpId = db.Mp.Pagamentos.Keys.Single();
        db.Mp.CancelarFalha = true; // o MP nao conseguiu cancelar o Pix: a loja paga mesmo assim

        await svc.BaixaManualAsync(c.Id, new BaixaManualRequest { Motivo = "transferencia" }, 1);
        Assert.Equal("manual", await db.Scalar<string>("SELECT pago_via FROM comercial_cobranca"));

        db.Mp.Aprovar(mpId);
        var cred = await db.Get<IMercadoPagoCredencialResolver>().RecebedorZippyAsync();
        await db.Get<IPagamentoSincronizador>().SincronizarAsync(cred, mpId, "webhook");
        Assert.Equal(249m, await db.Scalar<decimal>("SELECT valor_pago FROM comercial_cobranca"));
        Assert.Equal(249m, await db.Scalar<decimal>("SELECT valor_excedente FROM comercial_cobranca"));
        Assert.Equal(249m, await db.Scalar<decimal>("SELECT valor_pago_total FROM vw_assinatura_resumo"));
        Assert.Equal(1, await db.Scalar<long>("SELECT count(*) FROM estabelecimento_situacao_log WHERE motivo = 'assinatura'"));
    }

    [DeliveryDatabaseFact]
    public async Task InadimplenciaSuspendeEPagamentoReativaSozinho()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        var svc = db.Get<IAssinaturaService>();
        await svc.ContratarAsync(db.Loja, new ContratarRequest { Plano = "rota", IdempotencyKey = "k" }, 7);
        var c = (await svc.CobrancasAsync(db.Loja)).Single();
        // A fatura venceu ha 3 dias.
        await db.Execute("UPDATE comercial_cobranca SET vencimento = NOW() - INTERVAL '3 days'");
        var worker = db.Worker();
        await worker.RodarAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal("em_atraso", await db.Scalar<string>("SELECT financeiro FROM assinatura"));
        Assert.True(await db.Scalar<bool>("SELECT EXISTS (SELECT 1 FROM comercial_aviso WHERE tipo = 'atraso')"));
        Assert.True(await db.Scalar<bool>("SELECT EXISTS (SELECT 1 FROM comercial_aviso WHERE tipo = 'vencimento')"));

        await db.Execute("UPDATE comercial_cobranca SET vencimento = NOW() - INTERVAL '8 days'");
        await worker.RodarAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        await worker.RodarAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal("suspensa", await db.Scalar<string>("SELECT financeiro FROM assinatura"));
        Assert.Equal(1, await db.Scalar<long>("SELECT count(*) FROM comercial_aviso WHERE tipo = 'suspensa'"));
        var painel = await svc.PainelAsync(db.Loja, true);
        Assert.Equal("suspenso", painel.Assinatura!.Acesso);

        // Pagamento confirmado: volta na hora, sem esperar o worker.
        await svc.PagarAsync(db.Loja, c.Id, new PagarCobrancaRequest { Metodo = "pix" }, 7);
        var mpId = db.Mp.Pagamentos.Keys.Single();
        db.Mp.Aprovar(mpId);
        await db.Get<IPagamentoSincronizador>().SincronizarAsync(await db.Get<IMercadoPagoCredencialResolver>().RecebedorZippyAsync(), mpId, "webhook");
        Assert.Equal("em_dia", await db.Scalar<string>("SELECT financeiro FROM assinatura"));
        Assert.True(await db.Scalar<bool>("SELECT EXISTS (SELECT 1 FROM comercial_aviso WHERE tipo = 'reativada')"));
    }

    [DeliveryDatabaseFact]
    public async Task UpgradeCobraProporcionalDowngradeAgendaECancelamentoMantemAcesso()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        var svc = db.Get<IAssinaturaService>();
        await svc.ContratarAsync(db.Loja, new ContratarRequest { Plano = "rota", IdempotencyKey = "k" }, 7);
        var c = (await svc.CobrancasAsync(db.Loja)).Single();
        await svc.BaixaManualAsync(c.Id, new BaixaManualRequest { Motivo = "teste" }, 1);

        var previsao = await svc.AlterarAsync(db.Loja, new AlterarRequest { Plano = "turbo" }, 7);
        Assert.False(previsao.Confirmada);
        Assert.Equal(TiposMudanca.Upgrade, previsao.Previsao.Tipo);
        Assert.Equal(0, await db.Scalar<long>("SELECT count(*) FROM comercial_cobranca WHERE tipo = 'upgrade'"));
        var errado = await Assert.ThrowsAsync<PagamentoException>(() =>
            svc.AlterarAsync(db.Loja, new AlterarRequest { Plano = "turbo", Confirmar = true, ValorPrevisto = previsao.Previsao.ValorAgora + 1 }, 7));
        Assert.Equal(AssinaturaCodigos.PrevisaoDesatualizada, errado.Codigo);

        var upgrade = await svc.AlterarAsync(db.Loja, new AlterarRequest { Plano = "turbo", Confirmar = true, ValorPrevisto = previsao.Previsao.ValorAgora }, 7);
        Assert.NotNull(upgrade.Cobranca);
        // Sem pagar, nada muda; e continua uma assinatura so.
        Assert.Equal("rota", await db.Scalar<string>("SELECT plano_chave FROM assinatura"));
        await svc.BaixaManualAsync(upgrade.Cobranca!.Id, new BaixaManualRequest { Motivo = "teste" }, 1);
        Assert.Equal("turbo", await db.Scalar<string>("SELECT plano_chave FROM assinatura"));
        Assert.Equal(349m, await db.Scalar<decimal>("SELECT preco FROM assinatura"));
        Assert.Equal("turbo", await db.Scalar<string>("SELECT plano_comercial FROM estabelecimentos"));
        Assert.Equal(1, await db.Scalar<long>("SELECT count(*) FROM assinatura WHERE status IN ('pendente', 'ativa')"));

        var down = await svc.AlterarAsync(db.Loja, new AlterarRequest { Plano = "partida", Confirmar = true, ValorPrevisto = 0 }, 7);
        Assert.Equal(TiposMudanca.Downgrade, down.Previsao.Tipo);
        Assert.Equal("turbo", await db.Scalar<string>("SELECT plano_chave FROM assinatura"));
        Assert.Equal("partida", await db.Scalar<string>("SELECT mudanca_plano FROM assinatura"));

        // Fim do periodo: o ciclo novo ja nasce no plano menor e com o preco dele.
        await db.Execute("UPDATE assinatura SET periodo_fim = NOW() + INTERVAL '2 days'");
        var worker = db.Worker();
        await worker.RodarAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(149m, await db.Scalar<decimal>("SELECT valor FROM comercial_cobranca c JOIN assinatura_ciclo ci ON ci.id = c.id_ciclo WHERE ci.numero = 2"));
        await worker.RodarAsync(DateTimeOffset.UtcNow.AddDays(3), CancellationToken.None);
        Assert.Equal("partida", await db.Scalar<string>("SELECT plano_chave FROM assinatura"));
        Assert.Equal("partida", await db.Scalar<string>("SELECT plano_comercial FROM estabelecimentos"));

        // Cancelar: para a renovacao, sem estorno, mantem ate o fim, com motivo e ator.
        var cancelada = await svc.CancelarAsync(db.Loja, new CancelarRequest { Motivo = "fechando a loja" }, 7);
        Assert.Equal("ativa", cancelada.Status);
        Assert.NotNull(cancelada.Cancelamento);
        Assert.Equal(7, await db.Scalar<int>("SELECT cancelamento_por FROM assinatura"));
        Assert.Equal(0, await db.Scalar<long>("SELECT count(*) FROM pagamento_estorno"));
        await db.Execute("UPDATE assinatura SET periodo_fim = NOW() - INTERVAL '1 minute'");
        await worker.RodarAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal("encerrada", await db.Scalar<string>("SELECT status FROM assinatura"));
    }

    [DeliveryDatabaseFact]
    public async Task CompraAvulsaProtegidaContraDuploCliqueESoAtivaDepoisDePaga()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        var svc = db.Get<IAssinaturaService>();
        var produto = await db.Scalar<long>("SELECT id FROM produtos_avulsos LIMIT 1");
        var c1 = await svc.ComprarAsync(db.Loja, new CompraRequest { ProdutoAvulsoId = produto, Quantidade = 2, IdempotencyKey = "a" }, 7);
        var c2 = await svc.ComprarAsync(db.Loja, new CompraRequest { ProdutoAvulsoId = produto, Quantidade = 2, IdempotencyKey = "a" }, 7);
        var c3 = await svc.ComprarAsync(db.Loja, new CompraRequest { ProdutoAvulsoId = produto, Quantidade = 2, IdempotencyKey = "b" }, 7);
        Assert.Equal(c1.Compra.Id, c2.Compra.Id);
        Assert.Equal(c1.Compra.Id, c3.Compra.Id);
        Assert.Equal(158m, c1.Compra.ValorTotal);
        Assert.Equal(0, await db.Scalar<long>("SELECT count(*) FROM motoboy_pacotes"));

        await svc.BaixaManualAsync(c1.Cobranca.Id, new BaixaManualRequest { Motivo = "teste" }, 1);
        Assert.Equal("ativa", await db.Scalar<string>("SELECT status FROM compra_extra"));
        Assert.Equal(6, await db.Scalar<int>("SELECT quantidade FROM motoboy_pacotes"));

        // Funcionario pede, gerente aprova: vira compra aguardando pagamento.
        var s = await svc.SolicitarAsync(db.Loja, new SolicitacaoRequest { ProdutoAvulsoId = produto, Quantidade = 1, Motivo = "sexta cheia" }, 9);
        var s2 = await svc.SolicitarAsync(db.Loja, new SolicitacaoRequest { ProdutoAvulsoId = produto, Quantidade = 1 }, 9);
        Assert.Equal(s.Id, s2.Id);
        var d1 = await svc.DecidirAsync(db.Loja, s.Id, new DecisaoRequest { Aprovar = true }, 7);
        var d2 = await svc.DecidirAsync(db.Loja, s.Id, new DecisaoRequest { Aprovar = true }, 7);
        Assert.Equal(d1.Compra!.Compra.Id, d2.Compra!.Compra.Id);
        Assert.Equal("aguardando_pagamento", d1.Compra.Compra.Status);
        await Assert.ThrowsAsync<PagamentoException>(() => svc.DecidirAsync(db.Loja, s.Id, new DecisaoRequest { Aprovar = false }, 7));
    }

    [DeliveryDatabaseFact]
    public async Task NoTestePrimeiroCicloComecaNoFimDoTesteSemCobrancaAutomatica()
    {
        await using var db = await Banco.Criar(situacao: "teste");
        var svc = db.Get<IAssinaturaService>();
        var a = await svc.ContratarAsync(db.Loja, new ContratarRequest { Plano = "rota", IdempotencyKey = "k" }, 7);
        var fimTeste = await db.Scalar<DateTime>("SELECT teste_termina_em FROM estabelecimentos");
        Assert.Equal(APIBack.Payments.Assinaturas.FusoComercial.Data(new DateTimeOffset(fimTeste, TimeSpan.Zero)),
            APIBack.Payments.Assinaturas.FusoComercial.Data(a.PeriodoInicio!.Value));
        Assert.Empty(db.Mp.Criacoes); // ninguem cobrou nada
        Assert.Equal("teste", await db.Scalar<string>("SELECT situacao_comercial FROM estabelecimentos"));
        // Pago durante o teste: a loja continua no teste ate o ciclo comecar.
        var c = (await svc.CobrancasAsync(db.Loja)).Single();
        await svc.BaixaManualAsync(c.Id, new BaixaManualRequest { Motivo = "teste" }, 1);
        Assert.Equal("teste", await db.Scalar<string>("SELECT situacao_comercial FROM estabelecimentos"));
        await db.Worker().RodarAsync(DateTimeOffset.UtcNow.AddDays(16), CancellationToken.None);
    }

    private sealed class Banco : IAsyncDisposable
    {
        public Guid Loja { get; } = Guid.NewGuid();
        public MpFalso Mp { get; } = new();
        private ServiceProvider _sp = null!;
        private NpgsqlDataSource _source = null!;
        private readonly string _schema = "assinatura_" + Guid.NewGuid().ToString("N");
        private string _base = "";

        public T Get<T>() where T : notnull => _sp.CreateScope().ServiceProvider.GetRequiredService<T>();
        public AssinaturaWorker Worker() => ActivatorUtilities.CreateInstance<AssinaturaWorker>(_sp);

        public static async Task<Banco> Criar(string situacao)
        {
            APIBack.Infrastructure.DapperConfiguration.Configure();
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE"));
            if (options.Host is not ("localhost" or "127.0.0.1")) throw new InvalidOperationException("Use somente PostgreSQL local.");
            var db = new Banco { _base = options.ConnectionString };
            await using (var cx = new NpgsqlConnection(db._base)) { await cx.OpenAsync(); await cx.ExecuteAsync($"CREATE SCHEMA {db._schema}"); }
            options.SearchPath = db._schema;
            db._source = NpgsqlDataSource.Create(options.ConnectionString);

            await db.Execute(@"
CREATE TYPE modulo_enum AS ENUM ('GERAL','CARDAPIO','CARDAPIOWEB','PEDIDOS','CLIENTES','IMPRESSAO','IFOOD','WHATSAPP','MAPA','DELIVERY','REMUNERACAO','INTELIGENCIA','FINANCEIRO');
CREATE TABLE estabelecimentos (id uuid PRIMARY KEY, nome_fantasia text, situacao_comercial text NOT NULL, plano_comercial text, motoboys_base int NOT NULL DEFAULT 0,
  modulos_ativos modulo_enum[] NOT NULL DEFAULT '{}', operacao_iniciada_em timestamptz, teste_termina_em timestamptz);
CREATE TABLE conversas (id uuid PRIMARY KEY);
CREATE TABLE clientes (id uuid PRIMARY KEY);
CREATE TABLE cardapio_pedido_publico (id uuid PRIMARY KEY, id_estabelecimento uuid, status_pagamento text, updated_at timestamptz);
CREATE TABLE cardapio_web_config (id_estabelecimento uuid PRIMARY KEY, nome_publico text);
CREATE TABLE delivery_tracking_schema_versions (version text PRIMARY KEY);
CREATE TABLE usuario (id int PRIMARY KEY, nome text);
CREATE TABLE estabelecimento_situacao_log (id bigserial PRIMARY KEY, estabelecimento_id uuid, acao text, situacao_antes text, situacao_depois text, motivo text,
  usuario_id int, detalhes jsonb, criado_em timestamptz DEFAULT NOW());
CREATE TABLE pacotes_comerciais (chave text PRIMARY KEY, nome text, modulos_ativos modulo_enum[], motoboys_base int, preco numeric(10,2), preco_anual numeric(10,2),
  cor text, icone text, frase text, arquivado boolean DEFAULT FALSE, ordem int DEFAULT 1);
CREATE TABLE produtos_avulsos (id bigserial PRIMARY KEY, nome text, preco numeric(10,2), modulos_ativos modulo_enum[] DEFAULT '{}', motoboys int DEFAULT 0,
  cor text, arquivado boolean DEFAULT FALSE);
CREATE TABLE motoboy_pacotes (id bigserial PRIMARY KEY, estabelecimento_id uuid, quantidade int CHECK (quantidade IN (3, 7)), periodicidade text,
  inicio_utc timestamptz, fim_utc timestamptz, lancado_por int, lancado_em timestamptz DEFAULT NOW(), cancelado_em timestamptz, cancelado_por int);
INSERT INTO pacotes_comerciais VALUES
  ('partida','Partida','{GERAL,PEDIDOS}',0,149,1490,null,null,null,false,1),
  ('rota','Rota','{GERAL,PEDIDOS,MAPA}',0,249,2490,null,null,null,false,2),
  ('turbo','Turbo','{GERAL,PEDIDOS,MAPA,DELIVERY}',3,349,3490,null,null,null,false,3);
INSERT INTO produtos_avulsos (nome, preco, motoboys) VALUES ('+3 motoboys', 79, 3);
INSERT INTO usuario VALUES (7, 'Gerente'), (9, 'Funcionario');
INSERT INTO estabelecimentos (id, nome_fantasia, situacao_comercial, operacao_iniciada_em, teste_termina_em)
VALUES (@L, 'Loja', @S, NOW() - INTERVAL '5 days', CASE WHEN @S = 'teste' THEN NOW() + INTERVAL '10 days' END);", new { L = db.Loja, S = situacao });
            await db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261031_01_mercadopago_nucleo.sql")));
            await db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261102_60_mercadopago_orders.sql")));
            await db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261101_40_assinaturas.sql")));

            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payments:ChaveMestra"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["Payments:MercadoPago:Ambiente"] = "sandbox",
                ["Payments:MercadoPago:NotificationUrl"] = "https://api.teste/api/webhooks/mercadopago"
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            services.AddMemoryCache();
            services.AddSingleton<IConfiguration>(config);
            services.AddSingleton(db._source);
            services.AddPagamentosMercadoPago(config);
            services.AddAssinaturasZippy();
            services.AddHttpClient(MercadoPagoClient.NomeHttpClient).ConfigurePrimaryHttpMessageHandler(() => db.Mp);
            db._sp = services.BuildServiceProvider();

            var protector = db._sp.GetRequiredService<IPagamentoSegredoProtector>();
            var conta = Guid.NewGuid();
            await db.Execute(@"
INSERT INTO mp_conta_zippy (id, ambiente, mp_user_id, mp_user_id_mascarado, access_token_cifrado, public_key) VALUES (@C, 'sandbox', @U, '**7001', @T, 'TEST-pk-zippy')",
                new { C = conta, U = MpUserZippy, T = protector.Proteger("TOKEN-ZIPPY", SegredoContexto.ZippyAccessToken(conta)) });
            db.Mp.Collector = MpUserZippy;
            return db;
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

    /// <summary>MP falso para o Pix da Zippy: respeita X-Idempotency-Key e devolve o formato da API v1.</summary>
    private sealed class MpFalso : HttpMessageHandler
    {
        public sealed record Criacao(string? Authorization, string? Idempotencia, JsonObject Corpo);
        private long _proximo = 8_000_000_000;
        private readonly ConcurrentDictionary<string, long> _porChave = new();
        public ConcurrentDictionary<long, MpPagamento> Pagamentos { get; } = new();
        public List<Criacao> Criacoes { get; } = new();
        public long Collector { get; set; }
        public bool CancelarFalha { get; set; }

        public void Aprovar(long id)
        {
            var p = Pagamentos[id];
            p.Status = "approved";
            p.StatusDetail = "accredited";
            p.DateApproved = p.DateLastUpdated = DateTimeOffset.UtcNow.AddSeconds(1);
        }

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
                        Id = novo, Status = "pending", StatusDetail = "pending_waiting_transfer", CollectorId = Collector,
                        TransactionAmount = MpOrdersFalso.Valor(corpo!), CurrencyId = "BRL", LiveMode = false,
                        ExternalReference = MpOrdersFalso.Referencia(corpo!), PaymentMethodId = "pix", PaymentTypeId = "bank_transfer",
                        DateCreated = DateTimeOffset.UtcNow, DateLastUpdated = DateTimeOffset.UtcNow,
                        PointOfInteraction = new MpPointOfInteraction { TransactionData = new MpTransactionData { QrCode = "00020126pix" + novo, QrCodeBase64 = "iVBOR" } }
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
                    if (CancelarFalha) return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"bad_request\"}") };
                    pag.Status = "cancelled"; pag.StatusDetail = "by_collector"; pag.DateLastUpdated = DateTimeOffset.UtcNow;
                    return MpOrdersFalso.Resposta(pag);
                }
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"not_found\"}") };
        }

        private static HttpResponseMessage Json(object o) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
    }
}
