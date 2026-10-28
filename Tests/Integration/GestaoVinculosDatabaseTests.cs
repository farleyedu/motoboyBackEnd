using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Documentos;
using APIBack.GestaoLogica.Vinculos;
using Dapper;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// Vínculos contra PostgreSQL de verdade: aprendizado local imediato, divergência pelo histórico (sacola), comunidade e
/// curadoria (validar faz valer para outra loja), desfazer sem apagar.
/// </summary>
public class GestaoVinculosDatabaseTests
{
    private static readonly DateTimeOffset Dia = new(2026, 10, 26, 14, 32, 0, TimeSpan.FromHours(-3));

    private static DocumentoLido Cupom(string numero, params ItemLido[] itens) => new(OrigemDocumento.Xml,
        new FornecedorLido("75315333011807", "Atacadão"), Dia, numero, "3", null, itens, 0, itens.Sum(i => i.ValorTotal),
        Array.Empty<string>());

    private static ItemLido Linha(int n, string descricao, decimal qtd, string unidade, decimal total) =>
        new(n, descricao, qtd, unidade, total / qtd, 0, total);

    [DeliveryDatabaseFact]
    public async Task Aprende_na_hora_avisa_divergencia_e_so_vale_para_todos_apos_curadoria()
    {
        var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
        APIBack.Infrastructure.DapperConfiguration.Configure();
        var opcoes = new NpgsqlConnectionStringBuilder(conexao);
        if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
        var schema = "gestao_vinc_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(conexao)) { await admin.OpenAsync(); await admin.ExecuteAsync($"CREATE SCHEMA {schema}"); }
        opcoes.SearchPath = schema;
        var fonte = NpgsqlDataSource.Create(opcoes.ConnectionString);
        try
        {
            var loja = Guid.NewGuid();
            var outra = Guid.NewGuid();
            await using (var c = await fonte.OpenConnectionAsync())
            {
                await c.ExecuteAsync(@"CREATE TABLE estabelecimentos(id uuid PRIMARY KEY, timezone_iana text);
CREATE TABLE usuario(id integer PRIMARY KEY, nome text);
INSERT INTO estabelecimentos VALUES (@loja, 'America/Sao_Paulo'), (@outra, 'America/Sao_Paulo');
INSERT INTO usuario VALUES (7, 'Marina'), (8, 'Rafa'), (1, 'Zippy');", new { loja, outra });
                foreach (var m in new[] { "Delivery/20261105_01_gestao_fundacao.sql", "Delivery/20261106_01_gestao_documentos.sql",
                             "Inteligencia/20261019_01_ingredientes.sql", "Delivery/20261107_01_gestao_vinculos.sql",
                             "Delivery/20261107_01_gestao_vinculos.sql" })
                    await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", m)));
            }
            var documentos = new DocumentosService(fonte, new SemFoto());
            var vinculos = new VinculosService(fonte);
            var curadoria = new CuradoriaService(fonte);

            // 1ª compra: sacola em pacote de 50 e muçarela 2 kg. Nada conhecido: só "criar novo".
            var doc1 = await documentos.Registrar(loja, 7, Cupom("1001",
                Linha(1, "SAC KRAFT 30X40", 1, "PCT", 9.90m), Linha(2, "QJO MUSS FAT 2KG", 1, "PC", 79.90m)), null, default);
            var s1 = await vinculos.Sugerir(loja, doc1.Id, default);
            Assert.All(s1, s => Assert.Equal(OrigemSugestao.CriarNovo, s.Sugestoes[0].Origem));
            Assert.Equal(2000m, s1[1].Conversao!.Quantidade);
            Assert.NotNull(s1[0].Pergunta); // PCT sem "C/N": pergunta quantas vêm

            var sacola = await vinculos.Confirmar(loja, 7, doc1.Id, 1,
                new ConfirmarVinculoRequest(null, null, new NovoInsumoRequest("Sacola kraft", "un", "Embalagem"), 50), default);
            Assert.True(sacola.InsumoCriado);
            Assert.Equal(50m, sacola.Conversao.Quantidade);
            Assert.Null(sacola.Divergencia);
            Assert.NotNull(sacola.Vinculo.GlobalId); // virou candidato da curadoria
            // Confirmar de novo a mesma escolha não duplica.
            var deNovo = await vinculos.Confirmar(loja, 7, doc1.Id, 1,
                new ConfirmarVinculoRequest(sacola.Vinculo.InsumoId, null, null, 50), default);
            Assert.Equal(sacola.Vinculo.Id, deNovo.Vinculo.Id);

            // 2ª compra: a mesma sacola veio como "1 UN" a R$ 9,90 (protótipo). A loja conhece o item, mas não esta embalagem:
            // sem fator aprendido, 1 UN = 1 un, e o preço por unidade fica ~50× acima do histórico.
            var doc2 = await documentos.Registrar(loja, 7, Cupom("1002", Linha(1, "SAC KRAFT 30X40", 1, "UN", 9.90m)), null, default);
            var s2 = Assert.Single(await vinculos.Sugerir(loja, doc2.Id, default));
            Assert.Equal((OrigemSugestao.Loja, 0.95m, sacola.Vinculo.InsumoId), (s2.Sugestoes[0].Origem, s2.Sugestoes[0].Confianca, s2.Sugestoes[0].InsumoId));
            Assert.Null(s2.VinculoId);
            Assert.Equal(1m, s2.Conversao!.Quantidade);
            Assert.Equal(50m, s2.Divergencia!.FatorSugerido);
            // Confirmar com fator 1 avisa de novo; corrigir para 50 desfaz o anterior e o preço errado sai do histórico.
            var errada = await vinculos.Confirmar(loja, 7, doc2.Id, 1, new ConfirmarVinculoRequest(sacola.Vinculo.InsumoId, null, null, 1), default);
            Assert.Equal(50m, errada.Divergencia!.FatorSugerido);
            var certa = await vinculos.Confirmar(loja, 7, doc2.Id, 1, new ConfirmarVinculoRequest(sacola.Vinculo.InsumoId, null, null, 50), default);
            Assert.Null(certa.Divergencia);
            Assert.NotEqual(errada.Vinculo.Id, certa.Vinculo.Id);
            var todos = await vinculos.Listar(loja, "sac", true, default);
            Assert.Equal((2, 1), (todos.Count(v => v.Estado == "confirmado"), todos.Count(v => v.Estado == "desfeito")));
            Assert.Equal(sacola.Vinculo.Id, Assert.Single(await vinculos.Sugerir(loja, doc1.Id, default), s => s.Linha == 1).VinculoId);

            // Outra loja: o candidato não vale para ela como "global"; aparece como comunidade (1 loja).
            var doc3 = await documentos.Registrar(outra, 8, Cupom("2001", Linha(1, "SAC KRAFT 30X40", 2, "PCT", 19.80m)), null, default);
            var s3 = Assert.Single(await vinculos.Sugerir(outra, doc3.Id, default));
            Assert.Equal((OrigemSugestao.Comunidade, 0.64m, (decimal?)50m), (s3.Sugestoes[0].Origem, s3.Sugestoes[0].Confianca, s3.Sugestoes[0].Fator));
            Assert.Null(s3.Sugestoes[0].InsumoId);

            // Curadoria valida: passa a ser global para a outra loja.
            var candidato = Assert.Single(await curadoria.Candidatos(default));
            Assert.Equal(("Sacola kraft", 1), (candidato.Nome, candidato.Lojas));
            await curadoria.Validar(1, candidato.Id, new ValidarCatalogoRequest(null, "Embalagens", null), default);
            Assert.Equal("DUPLICADO", (await Assert.ThrowsAsync<GestaoException>(() =>
                curadoria.Rejeitar(1, candidato.Id, "tarde demais", default))).Codigo);
            var s4 = Assert.Single(await vinculos.Sugerir(outra, doc3.Id, default));
            Assert.Equal((OrigemSugestao.Global, 0.88m), (s4.Sugestoes[0].Origem, s4.Sugestoes[0].Confianca));
            Assert.Equal(100m, s4.Conversao!.Quantidade);

            // A outra loja importa o item global: vira insumo dela, ligado ao mesmo item.
            var importado = await vinculos.Confirmar(outra, 8, doc3.Id, 1, new ConfirmarVinculoRequest(null, candidato.Id, null, 50), default);
            Assert.True(importado.InsumoCriado);
            Assert.Equal(candidato.Id, importado.Vinculo.GlobalId);
            Assert.NotEqual(sacola.Vinculo.InsumoId, importado.Vinculo.InsumoId);

            // Desfazer exige motivo, não apaga e não desfaz duas vezes.
            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() =>
                vinculos.Desfazer(outra, 8, importado.Vinculo.Id, " ", default))).Codigo);
            Assert.Equal("desfeito", (await vinculos.Desfazer(outra, 8, importado.Vinculo.Id, "Errei o item", default)).Estado);
            Assert.Equal("JA_ESTORNADO", (await Assert.ThrowsAsync<GestaoException>(() =>
                vinculos.Desfazer(outra, 8, importado.Vinculo.Id, "de novo", default))).Codigo);
            await using (var c = await fonte.OpenConnectionAsync())
            {
                Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() => c.ExecuteAsync("DELETE FROM gestao_vinculos"))).SqlState);
                Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() =>
                    c.ExecuteAsync("UPDATE gestao_vinculo_precos SET preco_base = 0"))).SqlState);
                // 1ª loja: doc1, doc2 com fator 1 (vínculo desfeito, fora da mediana) e doc2 com fator 50; outra loja: doc3.
                Assert.Equal(4, await c.ExecuteScalarAsync<int>("SELECT count(*) FROM gestao_vinculo_precos"));
            }
        }
        finally
        {
            await fonte.DisposeAsync();
            await using var admin = new NpgsqlConnection(conexao); await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }

    private sealed class SemFoto : ILeitorCupomFoto
    {
        public Task<DocumentoLido> Ler(byte[] imagem, string mediaType, CancellationToken ct) => throw new NotSupportedException();
    }
}
