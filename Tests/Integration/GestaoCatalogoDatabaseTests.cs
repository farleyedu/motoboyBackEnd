using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Catalogo;
using APIBack.GestaoLogica.Compras;
using APIBack.GestaoLogica.Documentos;
using APIBack.GestaoLogica.Financeiro;
using APIBack.GestaoLogica.Vinculos;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// Integração 6B — Catálogo e Financeiro contra PostgreSQL de verdade: compra à mão de R$ 186,40 aparece no financeiro e na lista,
/// a mesma nota é barrada, o nome do cupom vira de-para, o produto novo vira da loja e candidato na curadoria, importar o global
/// duas vezes é DUPLICADO, finalidade guarda antes/depois, "não controlar" e a conta atrasada há meses aparece em "a pagar".
/// </summary>
public class GestaoCatalogoDatabaseTests
{
    private sealed class SemFoto : ILeitorCupomFoto
    {
        public Task<DocumentoLido> Ler(byte[] imagem, string mediaType, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Relogio(DateTimeOffset agora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => agora;
    }

    [DeliveryDatabaseFact]
    public async Task Compra_a_mao_catalogo_de_para_e_a_pagar()
    {
        var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
        APIBack.Infrastructure.DapperConfiguration.Configure();
        var opcoes = new NpgsqlConnectionStringBuilder(conexao);
        if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
        var schema = "gestao_cat_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(conexao)) { await admin.OpenAsync(); await admin.ExecuteAsync($"CREATE SCHEMA {schema}"); }
        opcoes.SearchPath = schema;
        var fonte = NpgsqlDataSource.Create(opcoes.ConnectionString);
        try
        {
            var loja = Guid.NewGuid();
            var bacon = Guid.NewGuid();
            await using (var c = await fonte.OpenConnectionAsync())
            {
                await c.ExecuteAsync(@"CREATE TABLE estabelecimentos(id uuid PRIMARY KEY, timezone_iana text);
CREATE TABLE usuario(id integer PRIMARY KEY, nome text);
INSERT INTO estabelecimentos VALUES (@loja, 'America/Sao_Paulo');
INSERT INTO usuario VALUES (7, 'Marina'), (1, 'Zippy');", new { loja });
                foreach (var m in new[] { "Delivery/20261105_01_gestao_fundacao.sql", "Delivery/20261106_01_gestao_documentos.sql",
                             "Inteligencia/20261019_01_ingredientes.sql", "Delivery/20261107_01_gestao_vinculos.sql",
                             "Delivery/20261108_01_gestao_estoque.sql", "Delivery/20261113_01_gestao_catalogo.sql",
                             "Delivery/20261113_01_gestao_catalogo.sql" })
                    await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", m)));
                await c.ExecuteAsync(@"
INSERT INTO gestao_catalogo_global (id, nome, nome_chave, unidade, categoria, status, criado_por, curado_por, curado_em, ilustracao)
VALUES (@bacon, 'Bacon fatiado', 'BACON FATIADO', 'g', 'Carnes', 'validado', 1, 1, now(), 'bacon');
INSERT INTO gestao_catalogo_kits (id, nome) VALUES (@kit, 'Hamburgueria');
INSERT INTO gestao_catalogo_kit_itens (kit_id, global_id) VALUES (@kit, @bacon);", new { bacon, kit = Guid.NewGuid() });
            }
            var relogio = new Relogio(new DateTimeOffset(2026, 10, 29, 15, 0, 0, TimeSpan.Zero));
            var livro = new AcontecimentosService(fonte, Array.Empty<IAcontecimentoAssinante>(), NullLogger<AcontecimentosService>.Instance, relogio);
            var documentos = new DocumentosService(fonte, new SemFoto());
            var compras = new ComprasService(fonte, livro, relogio);
            var lista = new ComprasListaService(fonte, documentos, compras, relogio);
            var vinculos = new VinculosService(fonte, relogio);
            var catalogo = new CatalogoService(fonte, vinculos, relogio);
            var financeiro = new FinanceiroService(fonte);

            // Compra à mão de R$ 186,40, a pagar: aparece no financeiro na hora e na lista do mês.
            var pedido = new CompraManualRequest("chave-1", "Atacadão", "75.315.333/0118-07", new DateOnly(2026, 10, 26), "1042",
                [new CompraManualItem("QJO MUSS FAT 2KG", 2, "PC", 79.90m), new CompraManualItem("DESENGORD 500ML", 1, "UN", 26.60m)],
                FormaCompra.APagar, null, Vencimento: new DateOnly(2026, 11, 10));
            var compra = await lista.RegistrarManual(loja, 7, pedido, default);
            Assert.Equal(186.40m, compra.Total);
            Assert.Equal(2, compra.Pendentes);
            Assert.Contains(compra.Acontecimento.Lancamentos, l => l.Valor == 186.40m && l.Status == "a_pagar");
            Assert.Equal(compra.DocumentoId, Assert.Single(await lista.Listar(loja, new DateOnly(2026, 10, 1), null, default)).DocumentoId);
            Assert.Single(await lista.Listar(loja, new DateOnly(2026, 11, 1), null, default)); // o vencimento cai em novembro

            // Repetir o envio devolve a mesma compra; a mesma nota com outra chave é barrada.
            Assert.Equal(compra.DocumentoId, (await lista.RegistrarManual(loja, 7, pedido, default)).DocumentoId);
            var dup = await Assert.ThrowsAsync<GestaoException>(() => lista.RegistrarManual(loja, 7, pedido with { ChaveIdempotencia = "chave-2" }, default));
            Assert.Equal("DUPLICADO", dup.Codigo);

            // De-para: os dois nomes lidos aparecem sem vínculo; vincular a um produto novo pelo Catálogo.
            var dePara = await catalogo.DeParaCompras(loja, default);
            Assert.Equal(2, dePara.Count);
            Assert.All(dePara, d => Assert.Null(d.InsumoId));
            var muss = dePara.Single(d => d.NomeChave == "QJO MUSS FAT 2KG");
            var vinculado = await catalogo.Vincular(loja, 7, new VincularDeParaRequest(muss.DocumentoId!.Value, muss.Linha!.Value, null, null,
                new CriarProdutoRequest("Muçarela", null, "Laticínios", "kg", ["Ingrediente"]), "1 PC = 2 kg", null), default);
            Assert.NotNull(vinculado.InsumoId);
            Assert.Equal((OrigemCatalogo.Novo, CuradoriaLoja.EmAnalise, "1 PC = 2 kg"), (vinculado.Origem, vinculado.Curadoria, vinculado.Conversao));
            var produtos = await catalogo.Produtos(loja, default);
            var mussarela = Assert.Single(produtos);
            Assert.Equal(new[] { "Ingrediente" }, mussarela.Finalidades);
            Assert.Equal(0m, mussarela.Estoque); // vínculo não é entrada de estoque
            Assert.Contains("QJO MUSS FAT 2KG", mussarela.NomesNoCupom);

            // "Não controlar" no desengordurante: a compra segue no financeiro, nada vai ao estoque.
            var deseng = dePara.Single(d => d.NomeChave == "DESENGORD 500ML");
            var marcado = await catalogo.NaoControlar(loja, 7, new NaoControlarRequest(null, deseng.NomeNoCupom, deseng.FornecedorCnpj), default);
            Assert.True(marcado.NaoControlar);

            // Importar do global e de novo: DUPLICADO. O kit diz que já existia.
            var importado = await catalogo.Importar(loja, 7, bacon, ["Ingrediente"], default);
            Assert.Equal((OrigemCatalogo.Global, "bacon", "Carnes"), (importado.Origem, importado.Ilustracao, importado.Categoria));
            Assert.Equal("DUPLICADO", (await Assert.ThrowsAsync<GestaoException>(() => catalogo.Importar(loja, 7, bacon, null, default))).Codigo);
            var global = await catalogo.Global(loja, "bac", default);
            Assert.True(Assert.Single(global.Produtos).NaLoja);
            Assert.Equal(["Bacon fatiado"], (await catalogo.ImportarKit(loja, 7, global.Kits.Single().Id, default)).JaExistiam);

            // Finalidade muda só na loja e guarda o antes/depois.
            var alterado = await catalogo.Finalidades(loja, 7, importado.Id, ["Ingrediente", "Montagem"], default);
            Assert.Contains(alterado.Historico, h => h.Titulo == "Finalidade alterada" && h.Detalhe!.Contains("Ingrediente → Ingrediente, Montagem"));

            // Busca da conferência acha o insumo da loja e o global.
            var busca = await catalogo.Buscar(loja, "bacon", default);
            Assert.Equal(importado.Id, Assert.Single(busca.Loja).InsumoId);
            Assert.Equal(importado.Id, Assert.Single(busca.Global).InsumoId);

            // Conta atrasada há meses aparece em "a pagar" (sem limite de um mês).
            await livro.Publicar(loja, 7, new PublicarAcontecimentoRequest
            {
                ChaveIdempotencia = "despesa-antiga", Tipo = "despesa", Modulo = ModuloGestao.Despesas,
                DataFato = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(-3)), Descricao = "Conserto da chapa",
                Lancamentos = [new LancamentoRequest { Natureza = Natureza.Despesa, Valor = 300, DataVencimento = new DateOnly(2026, 7, 10), Categoria = "manutenção" }],
            }, default);
            var aPagar = await financeiro.APagar(loja, new DateOnly(2026, 11, 5), default);
            Assert.Contains(aPagar, l => l.Valor == 300 && l.DataVencimento == new DateOnly(2026, 7, 10));
            Assert.DoesNotContain(aPagar, l => l.Valor == 186.40m); // vence em 10/11, depois do limite
            var resumo = await financeiro.Resumo(loja, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 29), default);
            Assert.Equal((300m, 1), (resumo.APagar.Atrasado, resumo.APagar.QuantidadeAtrasada));
        }
        finally
        {
            await fonte.DisposeAsync();
            await using var admin = new NpgsqlConnection(conexao);
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }
}
