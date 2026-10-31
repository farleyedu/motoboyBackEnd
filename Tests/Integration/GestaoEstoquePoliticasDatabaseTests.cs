using System.Text.Json;
using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Estoque;
using APIBack.Security;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// Integração 6B, Estoque: itens com saldo/mínimo/custo, motivos da loja, rascunho da contagem, contagem às cegas do operador
/// (vira divergência, nunca perda) e explicação de divergência sem linhas no livro. PostgreSQL local de verdade.
/// </summary>
public class GestaoEstoquePoliticasDatabaseTests
{
    private sealed class SemFicha : IFichaTecnicaLeitor
    {
        public Task<IReadOnlyDictionary<Guid, FichaTecnica>> Ler(Guid loja, IReadOnlyCollection<Guid> produtoIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, FichaTecnica>>(new Dictionary<Guid, FichaTecnica>());
    }

    [DeliveryDatabaseFact]
    public async Task Itens_minimo_motivos_rascunho_e_contagem_as_cegas()
    {
        var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
        APIBack.Infrastructure.DapperConfiguration.Configure();
        var opcoes = new NpgsqlConnectionStringBuilder(conexao);
        if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
        var schema = "gestao_estq_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(conexao)) { await admin.OpenAsync(); await admin.ExecuteAsync($"CREATE SCHEMA {schema}"); }
        opcoes.SearchPath = schema;
        var fonte = NpgsqlDataSource.Create(opcoes.ConnectionString);
        try
        {
            var loja = Guid.NewGuid();
            var queijo = Guid.NewGuid();
            var pao = Guid.NewGuid();
            await using (var c = await fonte.OpenConnectionAsync())
            {
                await c.ExecuteAsync(@"CREATE TABLE estabelecimentos(id uuid PRIMARY KEY, timezone_iana text);
CREATE TABLE usuario(id integer PRIMARY KEY, nome text);
INSERT INTO estabelecimentos VALUES (@loja, 'America/Sao_Paulo');
INSERT INTO usuario VALUES (7, 'Marina'), (8, 'Ana do caixa');", new { loja });
                foreach (var m in new[] { "Delivery/20261105_01_gestao_fundacao.sql", "Inteligencia/20261019_01_ingredientes.sql",
                             "Delivery/20261108_01_gestao_estoque.sql", "Delivery/20261112_01_gestao_estoque_politicas.sql" })
                    await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", m)));
                await c.ExecuteAsync(@"INSERT INTO insumo (id, estabelecimento_id, nome, unidade, criado_por) VALUES (@queijo, @loja, 'Muçarela', 'g', 7), (@pao, @loja, 'Pão', 'un', 7);
INSERT INTO insumo_custo (id, estabelecimento_id, insumo_id, quantidade_compra, unidade_compra, valor_compra, custo_base, criado_por)
VALUES (gen_random_uuid(), @loja, @queijo, 1000, 'g', 40, 0.04, 7);", new { loja, queijo, pao });
            }
            var livro = new AcontecimentosService(fonte, Array.Empty<IAcontecimentoAssinante>(), NullLogger<AcontecimentosService>.Instance);
            var estoque = new EstoqueService(fonte, livro, new SemFicha(), new ItensDoPedidoFake(fonte));
            var politicas = new EstoquePoliticasService(fonte);
            var gerente = GestaoPapeis.Resolver((_, _) => true, false);
            var operador = GestaoPapeis.Resolver((m, _) => m is "Caixa" or "Estoque", false);

            // Entrada de 5 kg de queijo (movimento confirmado por uma pessoa).
            await livro.Publicar(loja, 7, new PublicarAcontecimentoRequest
            {
                ChaveIdempotencia = "entrada-queijo", Tipo = "conferencia_item", Modulo = ModuloGestao.Compras, DataFato = DateTimeOffset.UtcNow,
                Descricao = "Entrada de queijo", ConfirmacaoHumana = true,
                Movimentos = new() { new MovimentoRequest { ItemId = queijo, ItemNome = "Muçarela", Unidade = "g", Tipo = TipoMovimento.Entrada, Quantidade = 5000 } },
            }, default);

            var itens = await politicas.Itens(loja, default);
            var q = itens.Single(i => i.InsumoId == queijo);
            Assert.Equal(5000m, q.Saldo);
            Assert.Equal(0.04m, q.CustoMedio);
            Assert.Null(itens.Single(i => i.InsumoId == pao).CustoMedio); // custo desconhecido nunca vira zero

            Assert.Equal(1500m, await politicas.DefinirMinimo(loja, 7, queijo, 1500, default));
            Assert.Equal(1500m, (await politicas.Itens(loja, default)).Single(i => i.InsumoId == queijo).Minimo);
            Assert.Null(await politicas.DefinirMinimo(loja, 7, queijo, null, default));
            Assert.Equal("NAO_ENCONTRADO", (await Assert.ThrowsAsync<GestaoException>(() =>
                politicas.DefinirMinimo(loja, 7, Guid.NewGuid(), 10, default))).Codigo);

            // Motivos: o do Zippy só liga/desliga; o da loja é criado e editado.
            var motivos = await politicas.SalvarMotivo(loja, 7, new MotivoSaidaRequest("cortesia", null, null, null, false), default);
            Assert.False(motivos.Single(m => m.Id == "cortesia").Ativo);
            motivos = await politicas.SalvarMotivo(loja, 7, new MotivoSaidaRequest(null, "Degustação", "uso", "#2DD4BF", true), default);
            var nova = motivos.Single(m => !m.Padrao);
            Assert.Equal("Degustação", nova.Rotulo);
            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() =>
                politicas.SalvarMotivo(loja, 7, new MotivoSaidaRequest(null, "X", "invalida", null, true), default))).Codigo);

            // Rascunho: um por loja, volta igual e some ao descartar.
            Assert.Null(await politicas.LerRascunho(loja, default));
            await politicas.SalvarRascunho(loja, 8, JsonSerializer.SerializeToElement(new { titulo = "Contagem de sexta", contados = new Dictionary<string, string> { [queijo.ToString()] = "4800" } }), default);
            Assert.Equal("Contagem de sexta", (await politicas.LerRascunho(loja, default))!.Dados.GetProperty("titulo").GetString());
            await politicas.DescartarRascunho(loja, default);
            Assert.Null(await politicas.LerRascunho(loja, default));

            // Contagem às cegas do operador: 4800 g contra 5000 esperados vira divergência de -200, nunca perda.
            var inv = await estoque.RegistrarInventario(loja, 8, operador, new InventarioRequest("inv-cego", new[] { new Contagem(queijo, 4800) }, null, true), default);
            var movimento = Assert.Single(inv.Acontecimento.Movimentos);
            Assert.Equal(TipoMovimento.Divergencia, movimento.Tipo);
            Assert.Equal(-200m, movimento.Quantidade);
            Assert.Equal(4800m, (await politicas.Itens(loja, default)).Single(i => i.InsumoId == queijo).Saldo);

            // Explicar a divergência não muda o estoque nem vira perda: acontecimento sem linhas apontando para o movimento.
            var explicacao = await livro.Publicar(loja, 7, new PublicarAcontecimentoRequest
            {
                ChaveIdempotencia = $"estoque:explicacao:{movimento.Id}", Tipo = "explicacao_divergencia", Modulo = ModuloGestao.Estoque,
                DataFato = DateTimeOffset.UtcNow, Descricao = "Divergência de Muçarela explicada · porcionamento",
                Origem = new OrigemRef { Tipo = "movimento_estoque", Id = movimento.Id.ToString() }, ConfirmacaoHumana = true,
            }, default);
            Assert.Empty(explicacao.Acontecimento.Movimentos);
            Assert.Empty(explicacao.Acontecimento.Lancamentos);
            Assert.Equal(4800m, (await politicas.Itens(loja, default)).Single(i => i.InsumoId == queijo).Saldo);
        }
        finally
        {
            await using var admin = new NpgsqlConnection(conexao);
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }
}
