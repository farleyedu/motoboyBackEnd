using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Compras;
using APIBack.GestaoLogica.Documentos;
using APIBack.GestaoLogica.Estoque;
using APIBack.GestaoLogica.Vinculos;
using APIBack.Security;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// Exemplos do prompt da Lógica contra PostgreSQL de verdade: compra de R$ 186,40 parcialmente conferida, correção posterior
/// com histórico, perda sem saída de caixa, venda que baixa insumos e embalagens, inventário que fica como divergência.
/// </summary>
public class GestaoComprasEstoqueDatabaseTests
{
    private static readonly Guid Burger = Guid.NewGuid();

    private sealed class FichaFake(Dictionary<Guid, FichaTecnica> fichas) : IFichaTecnicaLeitor
    {
        public Task<IReadOnlyDictionary<Guid, FichaTecnica>> Ler(Guid loja, IReadOnlyCollection<Guid> produtoIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, FichaTecnica>>(fichas.Where(f => produtoIds.Contains(f.Key)).ToDictionary(f => f.Key, f => f.Value));
    }

    private sealed class SemFoto : ILeitorCupomFoto
    {
        public Task<DocumentoLido> Ler(byte[] imagem, string mediaType, CancellationToken ct) => throw new NotSupportedException();
    }

    private static ItemLido Linha(int n, string descricao, decimal qtd, string unidade, decimal total) =>
        new(n, descricao, qtd, unidade, total / qtd, 0, total);

    [DeliveryDatabaseFact]
    public async Task Compra_parcial_correcao_perda_baixa_por_venda_e_inventario()
    {
        var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
        APIBack.Infrastructure.DapperConfiguration.Configure();
        var opcoes = new NpgsqlConnectionStringBuilder(conexao);
        if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
        var schema = "gestao_comp_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(conexao)) { await admin.OpenAsync(); await admin.ExecuteAsync($"CREATE SCHEMA {schema}"); }
        opcoes.SearchPath = schema;
        var fonte = NpgsqlDataSource.Create(opcoes.ConnectionString);
        try
        {
            var loja = Guid.NewGuid();
            var caixaKraft = Guid.NewGuid();
            await using (var c = await fonte.OpenConnectionAsync())
            {
                await c.ExecuteAsync(@"CREATE TABLE estabelecimentos(id uuid PRIMARY KEY, timezone_iana text);
CREATE TABLE usuario(id integer PRIMARY KEY, nome text);
CREATE TABLE pedido(id integer PRIMARY KEY, id_estabelecimento uuid);
CREATE TABLE pedido_item(id bigserial PRIMARY KEY, pedido_id integer, produto_id uuid, nome text, quantidade integer, ordem integer DEFAULT 0);
INSERT INTO estabelecimentos VALUES (@loja, 'America/Sao_Paulo');
INSERT INTO usuario VALUES (7, 'Marina'), (8, 'Ana do caixa');", new { loja });
                foreach (var m in new[] { "Delivery/20261105_01_gestao_fundacao.sql", "Delivery/20261106_01_gestao_documentos.sql",
                             "Inteligencia/20261019_01_ingredientes.sql", "Delivery/20261107_01_gestao_vinculos.sql",
                             "Delivery/20261108_01_gestao_estoque.sql", "Delivery/20261108_01_gestao_estoque.sql" })
                    await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", m)));
                await c.ExecuteAsync(@"INSERT INTO insumo (id, estabelecimento_id, nome, unidade, criado_por) VALUES (@caixaKraft, @loja, 'Caixa kraft', 'un', 7);
INSERT INTO pedido VALUES (501, @loja), (502, @loja), (503, @loja), (504, @loja), (900, @outra);
INSERT INTO pedido_item (pedido_id, produto_id, nome, quantidade) VALUES
  (501, @Burger, 'X-Burger', 2), (502, @Burger, 'X-Burger', 1), (503, @Burger, 'X-Burger', 1), (503, NULL, 'Refri avulso', 1),
  (504, @Burger, 'X-Burger', 1);", new { loja, caixaKraft, outra = Guid.NewGuid(), Burger });
            }
            var livro = new AcontecimentosService(fonte, Array.Empty<IAcontecimentoAssinante>(), NullLogger<AcontecimentosService>.Instance);
            var documentos = new DocumentosService(fonte, new SemFoto());
            var vinculos = new VinculosService(fonte);
            var compras = new ComprasService(fonte, livro);
            var gerente = GestaoPapeis.Resolver((_, _) => true, false);
            var operador = GestaoPapeis.Resolver((m, _) => m is "Caixa" or "Estoque", false);
            var operadorSemEstoque = GestaoPapeis.Resolver((m, _) => m is "Caixa", false);
            var cartao = (await livro.CriarConta(loja, 7, new ContaRequest { ChaveIdempotencia = "conta-inter", Tipo = TipoConta.CartaoEmpresa,
                Nome = "Crédito Inter", DiaFechamento = 5, DiaVencimento = 12 }, default)).Id;

            // Compra C-1042 resumida a 3 linhas que somam R$ 186,40.
            var doc = await documentos.Registrar(loja, 7, new DocumentoLido(OrigemDocumento.Xml, new FornecedorLido("75315333011807", "Atacadão"),
                new DateTimeOffset(2026, 10, 26, 14, 32, 0, TimeSpan.FromHours(-3)), "184552", "3", null, new[]
                {
                    Linha(1, "PAO HAMB BRIOCHE C/6 ZP", 4, "PCT", 51.60m), Linha(2, "QJO MUSS FAT 2KG", 1, "PC", 79.90m),
                    Linha(3, "DESENGORDURANTE 5L", 1, "UN", 54.90m),
                }, 0, 186.40m, Array.Empty<string>()), null, default);
            var pao = (await vinculos.Confirmar(loja, 7, doc.Id, 1,
                new ConfirmarVinculoRequest(null, null, new NovoInsumoRequest("Pão brioche", "un", null), 6), default)).Vinculo.InsumoId;
            var queijo = (await vinculos.Confirmar(loja, 7, doc.Id, 2,
                new ConfirmarVinculoRequest(null, null, new NovoInsumoRequest("Muçarela fatiada", "g", null), 2000), default)).Vinculo.InsumoId;

            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() =>
                compras.Conferir(loja, 7, doc.Id, 1, new ConferirItemRequest(null, false, true), default))).Codigo);

            // Crédito Inter 2x: a compra entra no financeiro inteira, em duas parcelas no cartão, sem tocar no estoque.
            var compra = await compras.Registrar(loja, 7, new RegistrarCompraRequest(doc.Id, FormaCompra.Cartao, cartao, 2), default);
            Assert.Equal(new[] { 93.20m, 93.20m }, compra.Acontecimento.Lancamentos.Select(l => l.Valor));
            Assert.All(compra.Acontecimento.Lancamentos, l => Assert.Equal(StatusLancamento.APagar, l.Status));
            Assert.Empty(compra.Acontecimento.Movimentos);
            Assert.Equal(new[] { StatusLinha.Pendente, StatusLinha.Pendente, StatusLinha.SemVinculo }, compra.Linhas.Select(l => l.Status));
            Assert.Equal(24m, compra.Linhas[0].QuantidadeEsperada);
            Assert.Equal(compra.AcontecimentoId,
                (await compras.Registrar(loja, 7, new RegistrarCompraRequest(doc.Id, FormaCompra.Cartao, cartao, 2), default)).AcontecimentoId);

            // Parcialmente conferida: o pão chegou, o desengordurante é consumo direto, a muçarela fica pendente.
            Assert.Equal("CONFIRMACAO_HUMANA", (await Assert.ThrowsAsync<GestaoException>(() =>
                compras.Conferir(loja, 7, doc.Id, 1, new ConferirItemRequest(null, false, false), default))).Codigo);
            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() =>
                compras.Conferir(loja, 7, doc.Id, 3, new ConferirItemRequest(null, false, true), default))).Codigo);
            await compras.Conferir(loja, 7, doc.Id, 1, new ConferirItemRequest(null, false, true), default);
            compra = await compras.Conferir(loja, 7, doc.Id, 3, new ConferirItemRequest(null, true, true), default);
            Assert.Equal((2, 1), (compra.Conferidas, compra.Pendentes));
            Assert.Equal((StatusLinha.Conferido, 24m), (compra.Linhas[0].Status, compra.Linhas[0].QuantidadeEntrada));
            Assert.Equal(StatusLinha.ConsumoDireto, compra.Linhas[2].Status);
            Assert.Null(compra.Linhas[2].MovimentoId);
            Assert.Equal("DUPLICADO", (await Assert.ThrowsAsync<GestaoException>(() =>
                compras.Conferir(loja, 7, doc.Id, 1, new ConferirItemRequest(4, false, true), default))).Codigo);
            var estoque = new EstoqueService(fonte, livro, new FichaFake(new()
            {
                [Burger] = new FichaTecnica(Burger, new[]
                {
                    new ComponenteFicha(pao, "Pão brioche", 1, "un"), new ComponenteFicha(queijo, "Muçarela fatiada", 30, "g"),
                    new ComponenteFicha(caixaKraft, "Caixa kraft", 1, "un", new[] { Modalidade.Delivery }),
                }),
            }));
            async Task<decimal> Saldo(Guid insumo) => (await estoque.Saldos(loja, default)).Single(s => s.InsumoId == insumo).Saldo;
            Assert.Equal((24m, 0m), (await Saldo(pao), await Saldo(queijo)));

            // Correção posterior: chegaram só 3 pacotes. Ajuste do movimento, com motivo; nada é apagado.
            var ajuste = await livro.Ajustar(loja, 7, new AjusteRequest
            {
                ChaveIdempotencia = "ajuste-pao", Motivo = "Chegaram só 3 pacotes", MovimentoId = compra.Linhas[0].MovimentoId,
                QuantidadeNova = 18, ConfirmacaoHumana = true,
            }, default);
            Assert.Equal(-6m, Assert.Single(ajuste.Acontecimento.Movimentos).Quantidade);
            compra = (await compras.Obter(loja, doc.Id, default))!;
            Assert.Equal(18m, compra.Linhas[0].QuantidadeEntrada);
            Assert.Equal(18m, await Saldo(pao));
            Assert.Equal(1, await Scalar(fonte, "SELECT count(*) FROM gestao_movimentos_estoque WHERE tipo = 'entrada'"));

            // Perda: sai do estoque, não do caixa. O operador também registra (hoje), sem Estoque:contar não.
            var lancamentosAntes = await Scalar(fonte, "SELECT count(*) FROM gestao_lancamentos");
            var perda = await estoque.RegistrarPerda(loja, 8, operador, new PerdaRequest("perda-1", pao, 2, "Caiu no chão", true), default);
            Assert.Empty(perda.Lancamentos);
            Assert.Equal(16m, await Saldo(pao));
            Assert.Equal(lancamentosAntes, await Scalar(fonte, "SELECT count(*) FROM gestao_lancamentos"));
            Assert.Equal("SEM_PERMISSAO", (await Assert.ThrowsAsync<GestaoException>(() =>
                estoque.RegistrarPerda(loja, 8, operadorSemEstoque, new PerdaRequest("perda-2", pao, 1, "x", true), default))).Codigo);

            // Venda que baixa insumos e embalagens: 501 (delivery, 2 lanches) e 502 (salão, sem caixa); 503 tem item avulso.
            var pedidos = new[] { new PedidoBaixa(501, Modalidade.Delivery), new PedidoBaixa(502, Modalidade.Salao), new PedidoBaixa(503, Modalidade.Retirada) };
            var previa = await estoque.PreviaBaixa(loja, pedidos, default);
            Assert.Equal(new[] { 501, 502 }, previa.Pedidos);
            Assert.Equal(503, Assert.Single(previa.SemFicha).PedidoId);
            Assert.Equal((2m, 3m, 90m), (previa.Consumo.Single(x => x.InsumoId == caixaKraft).Quantidade,
                previa.Consumo.Single(x => x.InsumoId == pao).Quantidade, previa.Consumo.Single(x => x.InsumoId == queijo).Quantidade));
            Assert.Equal(16m, await Saldo(pao)); // prévia não move estoque
            Assert.Equal("CONFIRMACAO_HUMANA", (await Assert.ThrowsAsync<GestaoException>(() =>
                estoque.RegistrarBaixa(loja, 7, gerente, new BaixaVendasRequest(pedidos, false), default))).Codigo);
            var baixa = await estoque.RegistrarBaixa(loja, 7, gerente, new BaixaVendasRequest(pedidos, true), default);
            Assert.Equal(3, baixa.Acontecimento.Movimentos.Count);
            Assert.All(baixa.Acontecimento.Movimentos, m => Assert.Equal(TipoMovimento.Saida, m.Tipo));
            Assert.Equal((13m, -90m, -2m), (await Saldo(pao), await Saldo(queijo), await Saldo(caixaKraft)));
            Assert.Equal("DUPLICADO", (await Assert.ThrowsAsync<GestaoException>(() =>
                estoque.RegistrarBaixa(loja, 7, gerente, new BaixaVendasRequest(new[] { new PedidoBaixa(501, Modalidade.Delivery), new PedidoBaixa(504, Modalidade.Delivery) }, true), default))).Codigo);
            Assert.Equal("NAO_ENCONTRADO", (await Assert.ThrowsAsync<GestaoException>(() =>
                estoque.PreviaBaixa(loja, new[] { new PedidoBaixa(900, Modalidade.Salao) }, default))).Codigo);

            // Inventário: diferença vira divergência (nunca perda), a contagem inteira fica no acontecimento.
            var inventario = await estoque.RegistrarInventario(loja, 7, gerente,
                new InventarioRequest("inv-1", new[] { new Contagem(pao, 12), new Contagem(queijo, 0) }, null, true), default);
            Assert.Equal(new[] { -1m, 90m }, inventario.Divergencias.Select(d => d.Diferenca));
            Assert.Equal(2, inventario.Acontecimento.Movimentos.Count);
            Assert.All(inventario.Acontecimento.Movimentos, m => Assert.Equal(TipoMovimento.Divergencia, m.Tipo));
            Assert.Equal((12m, 0m), (await Saldo(pao), await Saldo(queijo)));
            Assert.Equal(1, await Scalar(fonte, "SELECT count(*) FROM gestao_movimentos_estoque WHERE tipo = 'perda'")); // só a perda explícita
        }
        finally
        {
            await fonte.DisposeAsync();
            await using var admin = new NpgsqlConnection(conexao); await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }

    private static async Task<int> Scalar(NpgsqlDataSource fonte, string sql)
    {
        await using var c = await fonte.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<int>(sql);
    }
}
