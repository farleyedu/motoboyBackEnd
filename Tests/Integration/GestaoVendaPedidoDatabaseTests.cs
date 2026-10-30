using System.Text.Json;
using APIBack.Acontecimentos;
using APIBack.DTOs.Delivery;
using APIBack.GestaoLogica.Caixa;
using APIBack.GestaoLogica.Vendas;
using APIBack.Repository;
using APIBack.Security;
using APIBack.Service;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// 6C1-A contra PostgreSQL de verdade: toda conclusao de pedido publica exatamente uma venda no livro, na mesma transacao, com forma,
/// modalidade e conta certas; cancelamento autorizado depois da venda estorna uma vez so.
/// </summary>
public partial class DeliverySyncDatabaseTests
{
    private const string MigracoesGestao = "20261105_01_gestao_fundacao.sql|20261106_01_gestao_documentos.sql|20261107_01_gestao_vinculos.sql|20261110_01_gestao_caixa.sql";

    internal static async Task PrepararGestao(Func<string, object?, Task> executar, Guid loja)
    {
        APIBack.Infrastructure.DapperConfiguration.Configure();
        await executar(@"ALTER TABLE estabelecimentos ADD COLUMN IF NOT EXISTS timezone_iana text;
CREATE TABLE IF NOT EXISTS usuario(id integer PRIMARY KEY, nome text);
INSERT INTO usuario(id, nome) VALUES (7, 'Marina') ON CONFLICT DO NOTHING;
UPDATE estabelecimentos SET timezone_iana = 'America/Sao_Paulo' WHERE id = @loja;", new { loja });
        foreach (var m in MigracoesGestao.Split('|'))
            await executar(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", m)), null);
    }

    private static AcontecimentosService Livro(NpgsqlDataSource fonte) =>
        new(fonte, Array.Empty<IAcontecimentoAssinante>(), NullLogger<AcontecimentosService>.Instance);

    private static async Task<Database> VendaDatabase(bool abrirCaixa = true)
    {
        var db = await CompletionDatabase();
        await db.Execute(@"ALTER TABLE pedido ADD COLUMN entrega_manual_saiu_utc timestamptz, ADD COLUMN entrega_manual_entregue_utc timestamptz,
 ADD COLUMN entregador_nome text;
ALTER TABLE delivery_route_stops ADD COLUMN canceled_at_utc timestamptz, ADD COLUMN cancel_reason text;");
        await PrepararGestao((sql, p) => db.Execute(sql, p), db.StoreId);
        var livro = Livro(db.Source);
        await livro.CriarConta(db.StoreId, 7, new ContaRequest { ChaveIdempotencia = "maq", Tipo = TipoConta.Recebivel, Subtipo = "maquininha", Nome = "Stone" }, default);
        await livro.CriarConta(db.StoreId, 7, new ContaRequest { ChaveIdempotencia = "ifd", Tipo = TipoConta.Recebivel, Subtipo = "ifood", Nome = "iFood" }, default);
        if (abrirCaixa)
        {
            var caixa = new CaixaService(db.Source, livro);
            await caixa.Abrir(db.StoreId, 7, GestaoPapeis.Resolver((_, _) => true, false), new AbrirCaixaRequest(await caixa.Hoje(db.StoreId, default), 100), default);
        }
        return db;
    }

    private sealed record Linha(string Natureza, decimal Valor, string Status, string? Conta, string? Subtipo);

    private static async Task<(Guid Id, JsonElement Dados, List<Linha> Linhas)> Venda(Database db, int pedido)
    {
        await using var c = await db.Source.OpenConnectionAsync();
        var a = await c.QuerySingleAsync<(Guid Id, string Dados)>(@"SELECT id, dados::text FROM gestao_acontecimentos
 WHERE tipo = 'venda' AND origem_tipo = 'pedido' AND origem_id = @p AND corrige_id IS NULL", new { p = pedido.ToString() });
        var linhas = (await c.QueryAsync<Linha>(@"SELECT l.natureza AS Natureza, l.valor AS Valor, l.status AS Status, k.tipo AS Conta, k.subtipo AS Subtipo
  FROM gestao_lancamentos l LEFT JOIN gestao_contas k ON k.id = l.conta_id WHERE l.acontecimento_id = @id ORDER BY l.linha", new { id = a.Id })).ToList();
        return (a.Id, JsonDocument.Parse(a.Dados).RootElement, linhas);
    }

    private static Task<int> Vendas(Database db) => db.Scalar<int>("SELECT count(*)::int FROM gestao_acontecimentos WHERE tipo = 'venda'");

    [DeliveryDatabaseFact]
    public async Task Venda6C1A_conclusao_do_app_publica_uma_venda_mista_com_dinheiro_na_gaveta_e_pix_na_maquininha()
    {
        await using var db = await VendaDatabase();
        var repo = new PedidoQueueRepository(db.Source); var request = CompletionRequest();
        await repo.CompleteDeliveryAsync(db.StoreId, 1, 7, db.SessionId, 4, request, default);
        await repo.CompleteDeliveryAsync(db.StoreId, 1, 7, db.SessionId, 4, request, default); // replay do app

        Assert.Equal(1, await Vendas(db));
        var (_, dados, linhas) = await Venda(db, 23);
        Assert.Equal("delivery", dados.GetProperty("modalidade").GetString());
        Assert.Equal("din", dados.GetProperty("forma").GetString()); // a maior parte
        Assert.Equal(2, dados.GetProperty("pagamentos").GetArrayLength());
        Assert.Equal(86.90m, linhas.Sum(l => l.Valor)); // total sem perder nem duplicar parcela
        Assert.Contains(new Linha("receita", 20m, "recebido", "recebivel", "maquininha"), linhas);
        Assert.Contains(new Linha("receita", 66.90m, "recebido", "caixa", null), linhas);
        Assert.Equal(7, await db.Scalar<int>("SELECT responsavel_id FROM gestao_acontecimentos WHERE tipo = 'venda'"));
        // A gaveta recebeu so o dinheiro (fundo 100 + 66,90): o Caixa le o saldo do livro.
        Assert.Equal(166.90m, (await Livro(db.Source).Contas(db.StoreId, default)).Single(k => k.Nome == "Gaveta do caixa").Saldo);
    }

    [DeliveryDatabaseFact]
    public async Task Venda6C1A_operador_conclui_em_dinheiro_sem_caixa_aberto_e_nao_inventa_recebimento()
    {
        await using var db = await VendaDatabase(abrirCaixa: false);
        await db.Execute("UPDATE pedido SET tipo_pagamento = 'Dinheiro' WHERE id = 23");
        var repo = new PedidoQueueRepository(db.Source);
        await repo.CompleteCurrentAsync(db.StoreId, 7, 1);
        await repo.CompleteCurrentAsync(db.StoreId, 7, 1); // conclui o 24 (pago, sem forma): a receber, a conferir

        Assert.Equal(2, await Vendas(db));
        var (_, dados, linhas) = await Venda(db, 23);
        Assert.Equal(("din", "delivery"), (dados.GetProperty("forma").GetString(), dados.GetProperty("modalidade").GetString()));
        Assert.Equal(new[] { new Linha("receita", 86.90m, "a_receber", null, null) }, linhas);
        Assert.Equal("sem_conta_caixa", dados.GetProperty("pendencias")[0].GetString());
        var (_, dados24, linhas24) = await Venda(db, 24);
        Assert.False(dados24.TryGetProperty("forma", out _));
        Assert.Equal(new[] { new Linha("receita", 64m, "a_receber", null, null) }, linhas24);
        Assert.Equal(0, await db.Scalar<int>("SELECT count(*)::int FROM gestao_contas WHERE tipo = 'caixa'"));
    }

    [DeliveryDatabaseFact]
    public async Task Venda6C1A_retirada_no_balcao_tem_modalidade_retirada_e_cartao_fica_a_receber_na_maquininha()
    {
        await using var db = await VendaDatabase();
        await db.Execute("INSERT INTO pedido(id,id_estabelecimento,status_pedido,nome_cliente,value,tipo_pagamento,status_pagamento) VALUES (30,@s,0,'Balcão','40,50','cartao_entrega','Pendente')",
            new { s = db.StoreId });
        var repo = new PedidoQueueRepository(db.Source);
        await repo.MarcarEntregueManualAsync(db.StoreId, 7, 30);
        await repo.MarcarEntregueManualAsync(db.StoreId, 7, 30); // repetir nao publica de novo

        Assert.Equal(1, await Vendas(db));
        var (_, dados, linhas) = await Venda(db, 30);
        Assert.Equal(("retirada", "cre"), (dados.GetProperty("modalidade").GetString(), dados.GetProperty("forma").GetString()));
        Assert.False(dados.GetProperty("pagamentos")[0].GetProperty("formaConfirmada").GetBoolean());
        Assert.Equal(new[] { new Linha("receita", 40.50m, "a_receber", "recebivel", "maquininha") }, linhas);
    }

    [DeliveryDatabaseFact]
    public async Task Venda6C1A_erro_no_livro_desfaz_a_conclusao()
    {
        await using var db = await VendaDatabase();
        var livro = new Mock<IAcontecimentosService>();
        livro.Setup(l => l.Publicar(It.IsAny<NpgsqlConnection>(), It.IsAny<NpgsqlTransaction>(), It.IsAny<Guid>(), It.IsAny<int?>(),
            It.IsAny<PublicarAcontecimentoRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(new GestaoException("ERRO_LIVRO", "falhou", 500));
        var repo = new PedidoQueueRepository(db.Source, null, livro.Object);

        Assert.Equal("VENDA_NAO_REGISTRADA", (await Assert.ThrowsAsync<DeliveryDomainException>(() => repo.CompleteCurrentAsync(db.StoreId, 7, 1))).Code);
        Assert.Equal(2, await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id = 23"));
        Assert.Equal("en_route", await db.Scalar<string>("SELECT stop_status FROM delivery_route_stops WHERE pedido_id = 23"));
        Assert.Equal(0, await Vendas(db));
    }

    [DeliveryDatabaseFact]
    public async Task Venda6C1A_cancelar_antes_nao_publica_e_cancelar_depois_estorna_uma_vez()
    {
        await using var db = await VendaDatabase();
        var repo = new PedidoQueueRepository(db.Source);
        await repo.CancelAsync(db.StoreId, 7, 24, "Cliente desistiu");
        Assert.Equal(0, await Vendas(db));

        await repo.CompleteCurrentAsync(db.StoreId, 7, 1);
        Assert.Equal("CANNOT_CANCEL_COMPLETED", (await Assert.ThrowsAsync<DeliveryDomainException>(() => repo.CancelAsync(db.StoreId, 7, 23, null))).Code);
        Assert.Equal("MOTIVO_OBRIGATORIO", (await Assert.ThrowsAsync<DeliveryDomainException>(() => repo.CancelarConcluidoAsync(db.StoreId, 7, 23, " "))).Code);
        await db.Execute("INSERT INTO pedido(id,id_estabelecimento,status_pedido,value) VALUES (31,@s,0,'10')", new { s = db.StoreId });
        Assert.Equal("PEDIDO_NAO_CONCLUIDO", (await Assert.ThrowsAsync<DeliveryDomainException>(() => repo.CancelarConcluidoAsync(db.StoreId, 7, 31, "Teste"))).Code);
        Assert.Equal("cancelado", await repo.CancelarConcluidoAsync(db.StoreId, 7, 23, "Cliente devolveu o pedido"));
        Assert.Equal("cancelado", await repo.CancelarConcluidoAsync(db.StoreId, 7, 23, "Cliente devolveu o pedido"));

        var (venda, _, _) = await Venda(db, 23);
        Assert.Equal(4, await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id = 23"));
        Assert.Equal(1, await Vendas(db)); // a venda original continua no livro
        await using var c = await db.Source.OpenConnectionAsync();
        var estorno = await c.QuerySingleAsync<(Guid CorrigeId, string Motivo, int Responsavel)>(
            "SELECT corrige_id, motivo, responsavel_id FROM gestao_acontecimentos WHERE correcao = 'estorno'");
        Assert.Equal((venda, "Cliente devolveu o pedido", 7), estorno);
        Assert.Equal(0m, await c.ExecuteScalarAsync<decimal>(@"SELECT sum(l.valor) FROM gestao_lancamentos l
 JOIN gestao_acontecimentos a ON a.id = l.acontecimento_id WHERE a.id = @venda OR a.corrige_id = @venda", new { venda }));
    }

    [DeliveryDatabaseFact]
    public async Task Venda6C1A_replay_concorrente_publica_uma_vez_e_loja_b_nao_alcanca_o_pedido()
    {
        await using var db = await VendaDatabase();
        await db.Execute("UPDATE pedido SET status_pedido = 3, tipo_pagamento = 'pix' WHERE id = 23");
        var lojaB = Guid.NewGuid();
        await db.Execute("INSERT INTO estabelecimentos(id, ativo, status, timezone_iana) VALUES (@lojaB, TRUE, 'ativo', 'America/Sao_Paulo')", new { lojaB });
        var livro = Livro(db.Source);

        async Task<Guid?> Publicar(Guid loja)
        {
            await using var c = await db.Source.OpenConnectionAsync();
            await using var tx = await c.BeginTransactionAsync();
            var id = await VendaDoPedido.PublicarAsync(livro, c, tx, loja, 23, 7, ModalidadeVenda.Delivery);
            await Task.Delay(50);
            await tx.CommitAsync();
            return id;
        }
        var ids = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Publicar(db.StoreId)));
        Assert.Single(ids.Distinct());
        Assert.Equal(ids[0], await Publicar(db.StoreId)); // replay do worker
        Assert.Equal(1, await Vendas(db));

        Assert.Equal("PEDIDO_NOT_FOUND", (await Assert.ThrowsAsync<DeliveryDomainException>(() => Publicar(lojaB))).Code);
        Assert.Equal("PEDIDO_NOT_FOUND", (await Assert.ThrowsAsync<DeliveryDomainException>(() =>
            new PedidoQueueRepository(db.Source).CancelarConcluidoAsync(lojaB, 7, 23, "Outra loja"))).Code);
        Assert.Equal(0, await db.Scalar<int>($"SELECT count(*)::int FROM gestao_acontecimentos WHERE estabelecimento_id = '{lojaB}'"));
    }

    [DeliveryDatabaseFact]
    public async Task Venda6C1A_pedido_ifood_separa_o_pre_pago_a_receber_do_cobrado_na_entrega()
    {
        await using var db = await VendaDatabase();
        await db.Execute(@"CREATE TABLE ifood_order_links(merchant_id text, external_order_id text, estabelecimento_id uuid, pedido_id int);
CREATE TABLE ifood_order_snapshots(merchant_id text, external_order_id text, estabelecimento_id uuid, order_amount numeric, prepaid_amount numeric, order_type text);
INSERT INTO ifood_order_links VALUES ('m', 'o', @s, 23); INSERT INTO ifood_order_snapshots VALUES ('m', 'o', @s, 70, 50, 'DELIVERY');
UPDATE pedido SET status_pedido = 3 WHERE id = 23;", new { s = db.StoreId });
        await using (var c = await db.Source.OpenConnectionAsync())
        {
            await using var tx = await c.BeginTransactionAsync();
            await VendaDoPedido.PublicarAsync(Livro(db.Source), c, tx, db.StoreId, 23, 7, ModalidadeVenda.Delivery,
                new[] { new DeliveryPaymentPart { Method = "dinheiro", Amount = 20, ReceivedConfirmed = true } });
            await tx.CommitAsync();
        }
        var (_, dados, linhas) = await Venda(db, 23);
        Assert.Equal(("ifood", "DELIVERY"), (dados.GetProperty("forma").GetString(), dados.GetProperty("ifoodTipo").GetString()));
        Assert.Contains(new Linha("receita", 50m, "a_receber", "recebivel", "ifood"), linhas);
        Assert.Contains(new Linha("receita", 20m, "recebido", "caixa", null), linhas);
        Assert.Equal(70m, linhas.Sum(l => l.Valor));
    }
}

/// <summary>6C1-A pelo fluxo oficial: a retirada iFood confirmada publica a venda (retirada) uma vez, mesmo com o worker repetindo.</summary>
public sealed partial class IfoodIntegrationSchemaDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task Venda6C1A_retirada_ifood_oficial_publica_uma_venda()
    {
        await using var db = await OfficialPickupDatabase();
        await DeliverySyncDatabaseTests.PrepararGestao((sql, p) => db.Execute(sql, p), db.StoreA);
        var request = await OfficialPickupRequest(db); await EnqueueOfficialPickup(db, request);
        var service = CompletionService(db, new CompletionClient());
        await service.ProcessAsync(default); await service.ProcessAsync(default);
        await new PedidoQueueRepository(db.Source).RecoverIfoodCompletionsAsync(default);

        Assert.Equal("succeeded", (await service.GetForStoreAsync(db.StoreA, request.OperationId, default))!.State);
        Assert.Equal(1, await db.Scalar<int>("SELECT count(*)::int FROM gestao_acontecimentos WHERE tipo = 'venda' AND origem_id = '100'"));
        Assert.Equal("retirada", await db.Scalar<string>("SELECT dados->>'modalidade' FROM gestao_acontecimentos WHERE tipo = 'venda'"));
        Assert.Equal(65m, await db.Scalar<decimal>("SELECT sum(valor) FROM gestao_lancamentos"));
        Assert.Equal(45m, await db.Scalar<decimal>("SELECT sum(valor) FROM gestao_lancamentos WHERE descricao = 'Pagamento: din'"));
    }
}
