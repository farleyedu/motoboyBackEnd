using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Payments.InfinitePay.Webhooks;
using APIBack.Payments.MercadoPago;
using APIBack.Payments.Recebimentos;
using Dapper;
using Moq;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// Recebimentos online contra PostgreSQL real (TEST_DELIVERY_DATABASE), com as migrations de verdade. Prova: estorno informado nao mexe na
/// cobranca, repetir nao duplica, confirmar acontece uma vez e so com outra pessoa, loja nao enxerga loja, mensalidade fica de fora e a venda
/// do pedido aparece uma vez so.
/// </summary>
public sealed class RecebimentosOnlineDatabaseTests
{
    private const int Gerente = 7;
    private const int Suporte = 8;

    [DeliveryDatabaseFact]
    public async Task InformarNaoMudaACobrancaERepetirNaoDuplica()
    {
        await using var db = await Banco.Criar();
        var intent = await db.IntentPaga(db.Loja, 50m);
        var pedido = new InformarEstornoRequest { Valor = 20m, Motivo = "Cliente desistiu", IdempotencyKey = "k1" };

        var a = await db.Service.InformarEstornoAsync(intent, db.Loja, pedido, Gerente);
        var b = await db.Service.InformarEstornoAsync(intent, db.Loja, pedido, Gerente);

        Assert.Equal(a.Id, b.Id);
        Assert.Equal(StatusEstornoInformado.Pendente, a.Status);
        Assert.Equal(1, await db.Conta("pagamento_estorno_informado"));
        var (status, estornado) = await db.Intent(intent);
        Assert.Equal(StatusIntent.Pago, status);
        Assert.Equal(0m, estornado);
        Assert.Equal(SituacaoRecebimento.EstornoInformado, (await db.Service.DiagnosticoAsync(intent, db.Loja)).Recebimento.Situacao);
    }

    [DeliveryDatabaseFact]
    public async Task ConfirmarExigeOutraPessoaEAconteceUmaVez()
    {
        await using var db = await Banco.Criar();
        var intent = await db.IntentPaga(db.Loja, 50m);
        var e = await db.Service.InformarEstornoAsync(intent, db.Loja, new InformarEstornoRequest { Valor = 20m, Motivo = "Cliente desistiu", IdempotencyKey = "k1" }, Gerente);

        await Assert.ThrowsAsync<PagamentoException>(() => db.Service.ConfirmarEstornoAsync(e.Id, new ConfirmarEstornoRequest { Evidencia = "Comprovante 123" }, Gerente));
        await db.Service.ConfirmarEstornoAsync(e.Id, new ConfirmarEstornoRequest { Evidencia = "Comprovante 123" }, Suporte);
        await db.Service.ConfirmarEstornoAsync(e.Id, new ConfirmarEstornoRequest { Evidencia = "Comprovante 123" }, Suporte);

        var (status, estornado) = await db.Intent(intent);
        Assert.Equal(StatusIntent.EstornadoParcial, status);
        Assert.Equal(20m, estornado);
        Assert.Equal(1, await db.Conta("pagamento_auditoria WHERE acao = 'estorno_confirmado'"));
        await Assert.ThrowsAsync<PagamentoException>(() => db.Service.DescartarEstornoAsync(e.Id, db.Loja, Gerente));
    }

    [DeliveryDatabaseFact]
    public async Task LojaNaoEnxergaNemInformaNaCobrancaDeOutra()
    {
        await using var db = await Banco.Criar();
        var intent = await db.IntentPaga(db.OutraLoja, 50m);

        await Assert.ThrowsAsync<PagamentoException>(() => db.Service.DiagnosticoAsync(intent, db.Loja));
        await Assert.ThrowsAsync<PagamentoException>(() => db.Service.InformarEstornoAsync(intent, db.Loja,
            new InformarEstornoRequest { Valor = 10m, Motivo = "Cliente desistiu", IdempotencyKey = "k1" }, Gerente));
        Assert.Empty((await db.Service.ListarAsync(new FiltroRecebimentos { Loja = db.Loja })).Itens);
    }

    [DeliveryDatabaseFact]
    public async Task ListaMostraAVendaUmaVezESoCobrancaDaLoja()
    {
        await using var db = await Banco.Criar();
        var intent = await db.IntentPaga(db.Loja, 50m, pedidoId: 501);
        await db.Mensalidade(db.Loja, 99m);
        await db.Execute("INSERT INTO gestao_acontecimentos VALUES (gen_random_uuid(), @L, 'venda', 'pedido', '501', NULL)", new { L = db.Loja });

        var r = await db.Service.ListarAsync(new FiltroRecebimentos { Loja = db.Loja, Provedor = "infinitepay" });

        var item = Assert.Single(r.Itens);
        Assert.Equal(intent, item.Id);
        Assert.True(item.NoCaixa);
        Assert.Equal("••••7890", item.IdExternoMascarado);
        Assert.Equal(50m, item.Valor);
        Assert.Equal(52.5m, item.ValorComprovante);
        Assert.Equal(50m, Assert.Single(r.Somas).Pago);
    }

    private sealed class Banco : IAsyncDisposable
    {
        public Guid Loja { get; } = Guid.NewGuid();
        public Guid OutraLoja { get; } = Guid.NewGuid();
        public RecebimentosOnlineService Service { get; private set; } = null!;
        private readonly string _schema = "recebimentos_" + Guid.NewGuid().ToString("N");
        private string _base = "";
        private NpgsqlDataSource _source = null!;
        private readonly Guid _conta = Guid.NewGuid();
        private readonly Guid _versao = Guid.NewGuid();

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
CREATE TABLE clientes (id uuid PRIMARY KEY);
CREATE TABLE usuario (id int PRIMARY KEY, nome text);
CREATE TABLE conversas (id uuid PRIMARY KEY);
CREATE TABLE cardapio_pedido_publico (id uuid PRIMARY KEY, id_estabelecimento uuid, id_pedido int);
CREATE TABLE cardapio_pedido_reembolso (id uuid PRIMARY KEY, id_intent uuid, valor numeric, status text, concluido_em timestamptz, created_at timestamptz DEFAULT NOW(), updated_at timestamptz);
CREATE TABLE gestao_acontecimentos (id uuid PRIMARY KEY, estabelecimento_id uuid, tipo text, origem_tipo text, origem_id text, corrige_id uuid);
CREATE TABLE delivery_tracking_schema_versions (version text PRIMARY KEY);
CREATE TABLE delivery_realtime_outbox (event_id uuid PRIMARY KEY, event_name text, target_group text, estabelecimento_id uuid, motoboy_id int,
                                       aggregate_version bigint, payload jsonb, occurred_at_utc timestamptz);
INSERT INTO estabelecimentos VALUES (@L, 'Loja'), (@O, 'Outra');
INSERT INTO usuario VALUES (7, 'Ana'), (8, 'Bia');", new { L = db.Loja, O = db.OutraLoja });

            var migracoes = Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery");
            foreach (var arquivo in new[]
                     {
                         "20261031_01_mercadopago_nucleo.sql", "20261102_60_mercadopago_orders.sql", "20261104_10_pagamentos_multigateway.sql",
                         "20261129_01_infinitepay_checkout.sql", "20261129_03_infinitepay_webhook.sql", "20261201_01_pagamento_estorno_informado.sql"
                     })
                await db.Execute(File.ReadAllText(Path.Combine(migracoes, arquivo)));

            foreach (var loja in new[] { db.Loja, db.OutraLoja })
            {
                var conta = loja == db.Loja ? db._conta : Guid.NewGuid();
                var versao = loja == db.Loja ? db._versao : Guid.NewGuid();
                await db.Execute(@"
INSERT INTO infinitepay_conta_loja (id, id_estabelecimento, infinite_tag_normalizada, habilitada) VALUES (@Conta, @Loja, 'loja', TRUE);
INSERT INTO infinitepay_conta_versao (id, id_conta, id_estabelecimento, versao, infinite_tag_snapshot) VALUES (@Versao, @Conta, @Loja, 1, 'loja');",
                    new { Conta = conta, Versao = versao, Loja = loja });
            }

            var credenciais = new Mock<IMercadoPagoCredencialResolver>();
            credenciais.SetupGet(c => c.AmbienteAtivo).Returns("sandbox");
            db.Service = new RecebimentosOnlineService(db._source, credenciais.Object, Array.Empty<IPagamentoEfeitoHandler>(), Mock.Of<IInfinitePayWebhookProcessor>());
            return db;
        }

        public async Task Execute(string sql, object? args = null)
        {
            await using var cx = await _source.OpenConnectionAsync();
            await cx.ExecuteAsync(sql, args);
        }

        public async Task<int> Conta(string de)
        {
            await using var cx = await _source.OpenConnectionAsync();
            return await cx.ExecuteScalarAsync<int>($"SELECT count(*)::int FROM {de}");
        }

        public async Task<(string Status, decimal Estornado)> Intent(Guid id)
        {
            await using var cx = await _source.OpenConnectionAsync();
            return await cx.QuerySingleAsync<(string, decimal)>("SELECT status, valor_estornado FROM pagamento_intent WHERE id = @Id", new { Id = id });
        }

        /// <summary>Cobranca InfinitePay ja confirmada no servidor: em producao, mesmo com o painel em sandbox.</summary>
        public async Task<Guid> IntentPaga(Guid loja, decimal valor, int? pedidoId = null)
        {
            var id = Guid.NewGuid();
            var nsu = "zp_p_cardapio_" + id.ToString("N");
            await using var cx = await _source.OpenConnectionAsync();
            var versao = await cx.ExecuteScalarAsync<Guid>("SELECT id FROM infinitepay_conta_versao WHERE id_estabelecimento = @Loja", new { Loja = loja });
            await cx.ExecuteAsync(@"
INSERT INTO pagamento_intent (id, id_estabelecimento, ambiente, provedor, origem, conta_tipo, id_infinitepay_conta_versao, pedido_id, valor, descricao, external_reference, idempotency_key,
                              status, valor_pago, pago_em)
VALUES (@Id, @Loja, 'producao', 'infinitepay', 'cardapio', 'loja', @Versao, @Pedido, @Valor, 'Pedido', @Nsu, @Nsu, 'pago', @Valor, NOW());
INSERT INTO infinitepay_checkout (id, id_intent, id_estabelecimento, id_conta_versao, order_nsu, handle_snapshot, valor_centavos, usa_api_key, payload_json, payload_hash,
                                  confirmado_em, transaction_nsu, invoice_slug, capture_method, amount_confirmado_centavos, paid_amount_centavos)
VALUES (@Ck, @Id, @Loja, @Versao, @Nsu, 'loja', @Centavos, FALSE, '{}', 'h', NOW(), @Tx, 'slug1', 'pix', @Centavos, @Centavos + 250);", new
            {
                Id = id, Ck = Guid.NewGuid(), Loja = loja, Versao = versao, Pedido = pedidoId, Valor = valor, Nsu = nsu, Centavos = (long)(valor * 100), Tx = "tx-" + id.ToString("N")[..6] + "7890"
            });
            return id;
        }

        public Task Mensalidade(Guid loja, decimal valor)
        {
            var id = Guid.NewGuid();
            return Execute(@"
INSERT INTO pagamento_intent (id, id_estabelecimento, ambiente, origem, conta_tipo, valor, descricao, external_reference, idempotency_key, status, valor_pago, pago_em)
VALUES (@Id, @Loja, 'sandbox', 'assinatura', 'zippy', @Valor, 'Mensalidade', @Ref, @Ref, 'pago', @Valor, NOW())", new { Id = id, Loja = loja, Valor = valor, Ref = "zp_s_" + id.ToString("N") });
        }

        public async ValueTask DisposeAsync()
        {
            await _source.DisposeAsync();
            await using var cx = new NpgsqlConnection(_base);
            await cx.OpenAsync();
            await cx.ExecuteAsync($"DROP SCHEMA {_schema} CASCADE");
        }
    }
}
