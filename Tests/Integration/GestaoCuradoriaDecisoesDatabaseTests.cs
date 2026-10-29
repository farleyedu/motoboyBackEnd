using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Vinculos;
using Dapper;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// Curadoria Zippy contra PostgreSQL de verdade: casos gerados dos vínculos das lojas, aprovar/corrigir/rejeitar/reabrir sem
/// apagar histórico, efeito só no catálogo global (o vínculo de cada loja continua no mesmo insumo) e decisões imutáveis.
/// </summary>
public class GestaoCuradoriaDecisoesDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task Aprovar_corrigir_rejeitar_e_reabrir_nunca_apagam_historico()
    {
        var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
        APIBack.Infrastructure.DapperConfiguration.Configure();
        var opcoes = new NpgsqlConnectionStringBuilder(conexao);
        if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
        var schema = "gestao_cur_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(conexao)) { await admin.OpenAsync(); await admin.ExecuteAsync($"CREATE SCHEMA {schema}"); }
        opcoes.SearchPath = schema;
        var fonte = NpgsqlDataSource.Create(opcoes.ConnectionString);
        try
        {
            var (l1, l2, l3) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            var (muss, catu, req) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            await using (var c = await fonte.OpenConnectionAsync())
            {
                await c.ExecuteAsync(@"CREATE TABLE estabelecimentos(id uuid PRIMARY KEY, timezone_iana text, nome_fantasia text, cidade text);
CREATE TABLE usuario(id integer PRIMARY KEY, nome text);
INSERT INTO estabelecimentos VALUES (@l1, 'America/Sao_Paulo', 'Pizzaria do Zé', 'Goiânia'),
    (@l2, 'America/Sao_Paulo', 'Forno & Cia', 'Anápolis'), (@l3, 'America/Sao_Paulo', 'Burger X', 'Goiânia');
INSERT INTO usuario VALUES (1, 'Zippy'), (2, 'Marina');", new { l1, l2, l3 });
                foreach (var m in new[] { "Delivery/20261105_01_gestao_fundacao.sql", "Delivery/20261106_01_gestao_documentos.sql",
                             "Inteligencia/20261019_01_ingredientes.sql", "Delivery/20261107_01_gestao_vinculos.sql",
                             "Delivery/20261114_01_gestao_curadoria_decisoes.sql", "Delivery/20261114_01_gestao_curadoria_decisoes.sql" })
                    await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", m)));
                await c.ExecuteAsync(@"
INSERT INTO gestao_catalogo_global (id, nome, nome_chave, unidade, categoria, status, criado_por, curado_por, curado_em, criado_por_loja) VALUES
    (@muss, 'Muçarela fatiada', 'MUCARELA FATIADA', 'g', 'Laticínios', 'validado', 1, 1, now(), NULL),
    (@catu, 'Catupiry original', 'CATUPIRY ORIGINAL', 'g', 'Laticínios', 'validado', 1, 1, now(), NULL),
    (@req, 'Requeijão cremoso', 'REQUEIJAO CREMOSO', 'g', NULL, 'candidato', 2, NULL, NULL, @l3);
INSERT INTO gestao_vinculos (id, estabelecimento_id, nome_chave, descricao, unidade_cupom, insumo_id, global_id, unidade_base, fator, confirmado_por, confirmado_em) VALUES
    (gen_random_uuid(), @l1, 'QJO MUSS', 'QJO MUSS', 'KG', gen_random_uuid(), @muss, 'g', 1000, 2, now() - interval '9 days'),
    (gen_random_uuid(), @l2, 'QJO MUSS', 'QJO MUSS', 'KG', gen_random_uuid(), @muss, 'g', 1000, 2, now() - interval '5 days'),
    (gen_random_uuid(), @l1, 'REQ CREMOSO', 'REQ CREMOSO', 'UN', gen_random_uuid(), @catu, 'g', 1500, 2, now() - interval '8 days'),
    (gen_random_uuid(), @l2, 'REQ CREMOSO', 'REQ CREMOSO', 'UN', gen_random_uuid(), @catu, 'g', 1500, 2, now() - interval '7 days'),
    (gen_random_uuid(), @l3, 'REQ CREMOSO', 'REQ CREMOSO', 'UN', gen_random_uuid(), @req, 'g', 1500, 2, now() - interval '3 days');",
                    new { muss, catu, req, l3, l1, l2 });
            }
            var curadoria = new CuradoriaDecisoesService(fonte);
            var vinculosAntes = await Vinculos(fonte);

            // Casos gerados dos sinais: de-para, lojas divergem e produto candidato.
            var casos = (await curadoria.Casos(default)).ToDictionary(x => x.Id);
            Assert.Equal(TipoCaso.DePara, casos["texto:QJO MUSS"].Tipo);
            var contra = casos["texto:REQ CREMOSO"];
            Assert.Equal((TipoCaso.Contra, 3, 1), (contra.Tipo, contra.Lojas, contra.Divergencias));
            Assert.Equal(TipoCaso.Produto, casos["prod:" + req].Tipo);

            // Aprovar fora dos critérios exige motivo e fica marcado como exceção.
            Assert.Equal("MOTIVO_OBRIGATORIO", (await Assert.ThrowsAsync<GestaoException>(() => curadoria.Decidir(1, "Zippy",
                new DecidirCuradoriaRequest("texto:QJO MUSS", "aprovar", null, null, null), default))).Codigo);
            var aprovado = await curadoria.Decidir(1, "Zippy", new DecidirCuradoriaRequest("texto:QJO MUSS", "aprovar", null, "Caso claro", null), default);
            Assert.True(aprovado.Excecao);
            Assert.Equal(muss, aprovado.ProdutoGlobalId);
            Assert.Equal("CASO_JA_DECIDIDO", (await Assert.ThrowsAsync<GestaoException>(() => curadoria.Decidir(1, "Zippy",
                new DecidirCuradoriaRequest("texto:QJO MUSS", "rejeitar", null, "mudei de ideia", null), default))).Codigo);

            // Corrigir "lojas divergem" para o candidato: ele é validado; o vínculo de cada loja não muda (regra 6).
            var corrigido = await curadoria.Decidir(1, "Zippy", new DecidirCuradoriaRequest("texto:REQ CREMOSO", "corrigir", null,
                "REQ é requeijão genérico", new ProdutoGlobalEscolhido(req.ToString(), null, null)), default);
            Assert.Equal(("Catupiry original", "Requeijão cremoso"), (corrigido.Anterior, corrigido.Novo));
            Assert.Equal("validado", await Status(fonte, req));
            Assert.Equal(vinculosAntes, await Vinculos(fonte));

            // Reabrir: registro novo, o anterior fica; o catálogo volta a como estava.
            var reaberto = await curadoria.Reabrir(1, "Zippy", corrigido.Id, new ReabrirDecisaoRequest(null, "Marca errada"), default);
            Assert.Equal((corrigido.Id, "Vínculo corrigido", "Aberto"), (reaberto.CorrigeId!.Value, reaberto.Anterior, reaberto.Novo));
            Assert.Equal("candidato", await Status(fonte, req));
            Assert.Equal("REGISTRO_JA_REABERTO", (await Assert.ThrowsAsync<GestaoException>(() =>
                curadoria.Reabrir(1, "Zippy", corrigido.Id, new ReabrirDecisaoRequest(null, "de novo"), default))).Codigo);

            // Rejeitar o produto candidato e reabrir: volta a candidato.
            var rejeitado = await curadoria.Decidir(2, "Marina", new DecidirCuradoriaRequest("prod:" + req, "rejeitar", null, "Sem base", null), default);
            Assert.Equal("rejeitado", await Status(fonte, req));
            await curadoria.Reabrir(2, "Marina", rejeitado.Id, new ReabrirDecisaoRequest(null, "Lojas confirmaram de novo"), default);
            Assert.Equal("candidato", await Status(fonte, req));

            // Corrigir o produto = mesclar no escolhido; reabrir devolve os vínculos ao candidato.
            var mesclado = await curadoria.Decidir(1, "Zippy", new DecidirCuradoriaRequest("prod:" + req, "corrigir", null,
                "É o mesmo produto", new ProdutoGlobalEscolhido(catu.ToString(), null, null)), default);
            Assert.Equal("mesclado", await Status(fonte, req));
            await using (var c = await fonte.OpenConnectionAsync())
                Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT count(*) FROM gestao_vinculos WHERE global_id = @req", new { req }));
            await curadoria.Reabrir(1, "Zippy", mesclado.Id, new ReabrirDecisaoRequest(null, "Não era o mesmo"), default);
            Assert.Equal("candidato", await Status(fonte, req));
            Assert.Equal(vinculosAntes, await Vinculos(fonte));

            // Lote: o que já foi decidido fica de fora com o motivo.
            var lote = await curadoria.DecidirEmLote(1, "Zippy", new DecidirEmLoteRequest(
                new[] { "texto:QJO MUSS", "prod:" + req }, "observar", null, "Aguardar mais lojas"), default);
            Assert.Equal("prod:" + req, Assert.Single(lote.Registros).CasoId);
            Assert.Equal("Já foi decidido.", Assert.Single(lote.Plano.Excluidos).Motivo);
            Assert.NotNull(lote.Registros[0].Lote);

            // Histórico completo e imutável.
            var historico = await curadoria.Decisoes(default);
            Assert.Equal(8, historico.Count);
            Assert.Equal("aprovado", CuradoriaDecisoesRegras.EstadoDoCaso("texto:QJO MUSS", historico));
            Assert.Equal("aberto", CuradoriaDecisoesRegras.EstadoDoCaso("texto:REQ CREMOSO", historico));
            Assert.Equal("observacao", CuradoriaDecisoesRegras.EstadoDoCaso("prod:" + req, historico));
            await using (var c = await fonte.OpenConnectionAsync())
            {
                Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() =>
                    c.ExecuteAsync("UPDATE gestao_curadoria_decisoes SET motivo = 'x'"))).SqlState);
                Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() =>
                    c.ExecuteAsync("DELETE FROM gestao_curadoria_decisoes"))).SqlState);
            }

            var produtos = await curadoria.ProdutosGlobais("mucarela", default);
            Assert.Equal(muss, Assert.Single(produtos).Id);
        }
        finally
        {
            await fonte.DisposeAsync();
            await using var admin = new NpgsqlConnection(conexao); await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }

    private static async Task<string> Status(NpgsqlDataSource fonte, Guid id)
    {
        await using var c = await fonte.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<string>("SELECT status FROM gestao_catalogo_global WHERE id = @id", new { id }) ?? "";
    }

    /// <summary>Vínculo de cada loja: loja, texto, insumo e item global. A curadoria não pode mudar o insumo da loja.</summary>
    private static async Task<string> Vinculos(NpgsqlDataSource fonte)
    {
        await using var c = await fonte.OpenConnectionAsync();
        return string.Join("|", await c.QueryAsync<string>(
            "SELECT estabelecimento_id || ':' || nome_chave || ':' || insumo_id || ':' || global_id FROM gestao_vinculos ORDER BY 1"));
    }
}
