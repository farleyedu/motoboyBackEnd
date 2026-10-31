using System.Text.Json;
using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Estoque;
using APIBack.GestaoLogica.Vendas;
using APIBack.Security;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>Itens lidos direto de pedido_item (o leitor real, com iFood e de-para, é testado na 6C3-A) e venda gravada como a conclusão grava.</summary>
public sealed class ItensDoPedidoFake(NpgsqlDataSource fonte) : IItensVendidosLeitor
{
    public async Task<IReadOnlyList<ItemVendido>> Ler(Guid loja, IReadOnlyCollection<int> pedidoIds, CancellationToken ct)
    {
        await using var c = await fonte.OpenConnectionAsync(ct);
        return (await c.QueryAsync<ItemVendido>(
            "SELECT pedido_id AS PedidoId, produto_id AS ProdutoId, nome, quantidade FROM pedido_item WHERE pedido_id = ANY(@ids) ORDER BY pedido_id, ordem",
            new { ids = pedidoIds.ToArray() })).ToList();
    }

    public static Task<AcontecimentoPublicado> Vender(IAcontecimentosService livro, Guid loja, int pedidoId, string modalidade,
        DateTimeOffset? quando = null) => livro.Publicar(loja, 7, new PublicarAcontecimentoRequest
    {
        ChaveIdempotencia = $"venda:pedido:{pedidoId}", Tipo = "venda", Modulo = ModuloGestao.Pedidos, DataFato = quando ?? DateTimeOffset.UtcNow,
        Descricao = $"Pedido #{pedidoId}", Origem = new OrigemRef { Tipo = "pedido", Id = pedidoId.ToString() },
        Dados = JsonSerializer.SerializeToElement(new { modalidade, total = 40m }),
        Lancamentos = new() { new LancamentoRequest { Natureza = Natureza.Receita, Valor = 40m, Descricao = "Pagamento: a conferir" } },
    }, default);
}

/// <summary>6C3-B contra PostgreSQL de verdade: elegibilidade pela venda do livro, modalidade do servidor, concorrência e dia do operador.</summary>
public class GestaoBaixaVendasDatabaseTests
{
    private sealed class Fichas(Dictionary<Guid, FichaTecnica> mapa) : IFichaTecnicaLeitor
    {
        public Task<IReadOnlyDictionary<Guid, FichaTecnica>> Ler(Guid loja, IReadOnlyCollection<Guid> produtoIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, FichaTecnica>>(mapa.Where(f => produtoIds.Contains(f.Key)).ToDictionary(f => f.Key, f => f.Value));
    }

    private static async Task<int> Scalar(NpgsqlDataSource fonte, string sql)
    {
        await using var c = await fonte.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<int>(sql);
    }

    [DeliveryDatabaseFact]
    public async Task Baixa_so_de_venda_concluida_com_modalidade_do_servidor_e_sem_baixar_duas_vezes()
    {
        var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
        APIBack.Infrastructure.DapperConfiguration.Configure();
        var opcoes = new NpgsqlConnectionStringBuilder(conexao);
        if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
        var schema = "gestao_baixa_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(conexao)) { await admin.OpenAsync(); await admin.ExecuteAsync($"CREATE SCHEMA {schema}"); }
        opcoes.SearchPath = schema;
        opcoes.MaxPoolSize = 40;
        var fonte = NpgsqlDataSource.Create(opcoes.ConnectionString);
        try
        {
            var loja = Guid.NewGuid();
            var outraLoja = Guid.NewGuid();
            var burger = Guid.NewGuid();
            var pao = Guid.NewGuid();
            var caixa = Guid.NewGuid();
            await using (var c = await fonte.OpenConnectionAsync())
            {
                await c.ExecuteAsync(@"CREATE TABLE estabelecimentos(id uuid PRIMARY KEY, timezone_iana text);
CREATE TABLE usuario(id integer PRIMARY KEY, nome text);
CREATE TABLE pedido(id integer PRIMARY KEY, id_estabelecimento uuid, status_pedido integer NOT NULL DEFAULT 3);
CREATE TABLE pedido_item(id bigserial PRIMARY KEY, pedido_id integer, produto_id uuid, nome text, quantidade integer, ordem integer DEFAULT 0);
INSERT INTO estabelecimentos VALUES (@loja, 'America/Sao_Paulo'), (@outraLoja, 'America/Sao_Paulo');
INSERT INTO usuario VALUES (7, 'Marina'), (8, 'Ana do caixa');", new { loja, outraLoja });
                foreach (var m in new[] { "Delivery/20261105_01_gestao_fundacao.sql", "Inteligencia/20261019_01_ingredientes.sql", "Delivery/20261108_01_gestao_estoque.sql" })
                    await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", m)));
                await c.ExecuteAsync(@"INSERT INTO insumo (id, estabelecimento_id, nome, unidade, categoria, criado_por) VALUES
  (@pao, @loja, 'Pão brioche', 'un', 'Ingrediente', 7), (@caixa, @loja, 'Caixa kraft', 'un', 'Embalagem', 7);
INSERT INTO pedido (id, id_estabelecimento, status_pedido) VALUES (1, @loja, 3), (2, @loja, 3), (3, @loja, 3), (4, @loja, 3), (5, @loja, 3),
  (6, @loja, 4), (7, @loja, 3), (8, @loja, 3), (9, @loja, 1), (10, @loja, 3), (900, @outraLoja, 3);
INSERT INTO pedido_item (pedido_id, produto_id, nome, quantidade) VALUES
  (1, @burger, 'X-Burger', 2), (2, @burger, 'X-Burger', 1), (3, @burger, 'X-Burger', 1), (4, @burger, 'X-Burger', 1), (5, @burger, 'X-Burger', 1),
  (6, @burger, 'X-Burger', 1), (7, @burger, 'X-Burger', 1), (8, @burger, 'X-Burger', 1), (8, NULL, 'Refri avulso', 1), (9, @burger, 'X-Burger', 1),
  (10, @burger, 'X-Burger', 1), (900, @burger, 'X-Burger', 1);", new { loja, outraLoja, pao, caixa, burger });
            }
            var livro = new AcontecimentosService(fonte, Array.Empty<IAcontecimentoAssinante>(), NullLogger<AcontecimentosService>.Instance);
            var estoque = new EstoqueService(fonte, livro, new Fichas(new()
            {
                [burger] = new FichaTecnica(burger, new[]
                {
                    new ComponenteFicha(pao, "Pão brioche", 1, "un"),
                    new ComponenteFicha(caixa, "Caixa kraft", 1, "un", new[] { Modalidade.Delivery }),
                }),
            }), new ItensDoPedidoFake(fonte));
            var gerente = GestaoPapeis.Resolver((_, _) => true, false);
            var operador = GestaoPapeis.Resolver((m, _) => m is "Caixa" or "Estoque", false);
            async Task<decimal> Saldo(Guid insumo) => (await estoque.Saldos(loja, default)).Single(s => s.InsumoId == insumo).Saldo;

            // Vendas: 1 delivery (2 lanches), 2 salão, 3 retirada, 4 e 5 delivery, 6 cancelado, 7 estornada, 8 com item sem ficha,
            // 10 venda de ontem. 9 ainda não foi concluído (sem venda). 900 é de outra loja.
            await ItensDoPedidoFake.Vender(livro, loja, 1, Modalidade.Delivery);
            await ItensDoPedidoFake.Vender(livro, loja, 2, Modalidade.Salao);
            await ItensDoPedidoFake.Vender(livro, loja, 3, Modalidade.Retirada);
            await ItensDoPedidoFake.Vender(livro, loja, 4, Modalidade.Delivery);
            await ItensDoPedidoFake.Vender(livro, loja, 5, Modalidade.Delivery);
            await ItensDoPedidoFake.Vender(livro, loja, 6, Modalidade.Delivery);
            var venda7 = await ItensDoPedidoFake.Vender(livro, loja, 7, Modalidade.Delivery);
            await ItensDoPedidoFake.Vender(livro, loja, 8, Modalidade.Delivery);
            await ItensDoPedidoFake.Vender(livro, loja, 10, Modalidade.Delivery, DateTimeOffset.UtcNow.AddDays(-3));
            await livro.Estornar(loja, 7, venda7.Id, new EstornoRequest { ChaveIdempotencia = "estorno-7", Motivo = "Cancelado depois de entregue", ConfirmacaoHumana = true }, default);

            // O cliente não escolhe a modalidade: delivery leva a caixa, salão e retirada não.
            var previa = await estoque.PreviaBaixa(loja, new[] { 1, 2, 3 }, false, default);
            Assert.Equal(new[] { 1, 2, 3 }, previa.Pedidos);
            Assert.Equal((Modalidade.Delivery, Modalidade.Salao, Modalidade.Retirada), (previa.Modalidades[1], previa.Modalidades[2], previa.Modalidades[3]));
            Assert.Equal(4m, previa.Consumo.Single(x => x.InsumoId == pao).Quantidade);
            var emb = previa.Consumo.Single(x => x.InsumoId == caixa);
            Assert.Equal((2m, true), (emb.Quantidade, emb.Embalagem));
            Assert.False(previa.Consumo.Single(x => x.InsumoId == pao).Embalagem);

            // Sem venda (não concluído), cancelado, estornado e item sem ficha: nada é baixado; o motivo aparece.
            var ruins = await estoque.PreviaBaixa(loja, new[] { 6, 7, 8, 9 }, false, default);
            Assert.Empty(ruins.Pedidos);
            Assert.Empty(ruins.Consumo);
            Assert.Equal(new[] { "cancelado", "cancelado", "sem_venda" }, ruins.Ignorados.OrderBy(i => i.PedidoId).Select(i => i.Motivo));
            Assert.Equal(8, Assert.Single(ruins.SemFicha).PedidoId);
            Assert.Equal("NAO_ENCONTRADO", (await Assert.ThrowsAsync<GestaoException>(() => estoque.PreviaBaixa(loja, new[] { 900 }, false, default))).Codigo);
            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() =>
                estoque.RegistrarBaixa(loja, 7, gerente, new BaixaVendasRequest(new[] { 6, 7, 8, 9 }, true), default))).Codigo);

            // Listagem do dia: só vendas de hoje, sem cancelado nem estornado; sem ficha marcado.
            var hoje = AcontecimentoRegras.DiaLocal(DateTimeOffset.UtcNow, await livro.Fuso(loja, default));
            var dia = await estoque.Candidatos(loja, hoje, false, default);
            Assert.Equal(new[] { 1, 2, 3, 4, 5, 8 }, dia.Pedidos.Select(p => p.PedidoId).Order());
            Assert.Equal("sem_ficha", dia.Pedidos.Single(p => p.PedidoId == 8).Situacao);
            Assert.Equal("Refri avulso", Assert.Single(dia.Pedidos.Single(p => p.PedidoId == 8).SemFicha));
            Assert.Equal("pronto", dia.Pedidos.Single(p => p.PedidoId == 1).Situacao);
            Assert.Equal(new[] { 10 }, (await estoque.Candidatos(loja, hoje.AddDays(-3), false, default)).Pedidos.Select(p => p.PedidoId));

            // Operador só enxerga e baixa hoje (a venda 10 é de três dias atrás).
            Assert.DoesNotContain(10, (await estoque.Candidatos(loja, hoje.AddDays(-3), true, default)).Pedidos.Select(p => p.PedidoId));
            var antiga = await estoque.PreviaBaixa(loja, new[] { 10 }, true, default);
            Assert.Equal("fora_do_dia", Assert.Single(antiga.Ignorados).Motivo);

            // Confirmação humana, prévia velha e baixa de verdade.
            Assert.Equal("CONFIRMACAO_HUMANA", (await Assert.ThrowsAsync<GestaoException>(() =>
                estoque.RegistrarBaixa(loja, 7, gerente, new BaixaVendasRequest(new[] { 1, 2, 3 }, false), default))).Codigo);
            Assert.Equal("PREVIA_DESATUALIZADA", (await Assert.ThrowsAsync<GestaoException>(() =>
                estoque.RegistrarBaixa(loja, 7, gerente, new BaixaVendasRequest(new[] { 1, 2, 3 }, true, "assinatura-velha"), default))).Codigo);
            Assert.Equal(0, await Scalar(fonte, "SELECT count(*) FROM gestao_baixas_pedido"));
            var lancamentosAntes = await Scalar(fonte, "SELECT count(*) FROM gestao_lancamentos");
            var baixa = await estoque.RegistrarBaixa(loja, 7, gerente, new BaixaVendasRequest(new[] { 1, 2, 3 }, true, previa.Assinatura), default);
            Assert.Equal(3, baixa.Previa.Pedidos.Count);
            Assert.All(baixa.Acontecimento.Movimentos, m => Assert.Equal(TipoMovimento.Saida, m.Tipo));
            Assert.Equal((-4m, -2m), (await Saldo(pao), await Saldo(caixa)));
            Assert.Equal(3, await Scalar(fonte, "SELECT count(*) FROM gestao_baixas_pedido"));
            Assert.Equal(lancamentosAntes, await Scalar(fonte, "SELECT count(*) FROM gestao_lancamentos")); // baixa não é despesa nem caixa
            Assert.Equal("baixado", (await estoque.Candidatos(loja, hoje, false, default)).Pedidos.Single(p => p.PedidoId == 2).Situacao);
            Assert.Equal("DUPLICADO", (await Assert.ThrowsAsync<GestaoException>(() =>
                estoque.RegistrarBaixa(loja, 7, gerente, new BaixaVendasRequest(new[] { 3, 4 }, true), default))).Codigo);
            Assert.Equal((-4m, -2m), (await Saldo(pao), await Saldo(caixa)));

            // Dois usuários ao mesmo tempo, seleções diferentes que dividem o pedido 5: o 5 sai uma vez só.
            var a = estoque.RegistrarBaixa(loja, 7, gerente, new BaixaVendasRequest(new[] { 4, 5 }, true), default);
            var b = estoque.RegistrarBaixa(loja, 8, gerente, new BaixaVendasRequest(new[] { 5 }, true), default);
            var resultados = await Task.WhenAll(Tentar(a), Tentar(b));
            Assert.Equal(1, resultados.Count(r => r == "ok"));
            Assert.Contains("DUPLICADO", resultados);
            Assert.Equal(1, await Scalar(fonte, "SELECT count(*) FROM gestao_baixas_pedido WHERE pedido_id = 5"));
            var esperado = resultados[0] == "ok" ? -10m : -8m; // antes: pão -4 e caixa -2; a venceu: +4 e 5 (-4); b venceu: +só o 5 (-2)
            Assert.Equal(esperado, (await Saldo(pao)) + (await Saldo(caixa)));
        }
        finally
        {
            await fonte.DisposeAsync();
            await using var admin = new NpgsqlConnection(conexao); await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }

    private static async Task<string> Tentar(Task<BaixaVendasRegistrada> t)
    {
        try { await t; return "ok"; }
        catch (GestaoException ex) { return ex.Codigo; }
    }
}
