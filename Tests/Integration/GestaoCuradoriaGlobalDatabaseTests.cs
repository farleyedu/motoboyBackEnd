using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Vinculos;
using Dapper;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// L7 contra PostgreSQL de verdade: o super admin corrige produto global, kits e categorias e muda os critérios; cada correção
/// vira decisão imutável com antes/depois, nada se apaga e o vínculo/insumo de cada loja continua o mesmo (regra 6).
/// </summary>
public class GestaoCuradoriaGlobalDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task Corrigir_global_e_criterios_gera_historico_imutavel_sem_mexer_na_loja()
    {
        var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
        APIBack.Infrastructure.DapperConfiguration.Configure();
        var opcoes = new NpgsqlConnectionStringBuilder(conexao);
        if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
        var schema = "gestao_l7_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(conexao)) { await admin.OpenAsync(); await admin.ExecuteAsync($"CREATE SCHEMA {schema}"); }
        opcoes.SearchPath = schema;
        var fonte = NpgsqlDataSource.Create(opcoes.ConnectionString);
        try
        {
            var (l1, l2) = (Guid.NewGuid(), Guid.NewGuid());
            var (muss, catu, oleo, cand) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            await using (var c = await fonte.OpenConnectionAsync())
            {
                await c.ExecuteAsync(@"CREATE TABLE estabelecimentos(id uuid PRIMARY KEY, timezone_iana text, nome_fantasia text, cidade text);
CREATE TABLE usuario(id integer PRIMARY KEY, nome text);
INSERT INTO estabelecimentos VALUES (@l1, 'America/Sao_Paulo', 'Pizzaria do Zé', 'Goiânia'), (@l2, 'America/Sao_Paulo', 'Forno & Cia', 'Anápolis');
INSERT INTO usuario VALUES (1, 'Zippy'), (2, 'Marina');", new { l1, l2 });
                foreach (var m in new[] { "Delivery/20261105_01_gestao_fundacao.sql", "Delivery/20261106_01_gestao_documentos.sql",
                             "Inteligencia/20261019_01_ingredientes.sql", "Delivery/20261107_01_gestao_vinculos.sql",
                             "Delivery/20261113_01_gestao_catalogo.sql", "Delivery/20261114_01_gestao_curadoria_decisoes.sql",
                             "Delivery/20261117_01_gestao_curadoria_global.sql", "Delivery/20261117_01_gestao_curadoria_global.sql" })
                    await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", m)));
                await c.ExecuteAsync(@"
INSERT INTO gestao_catalogo_global (id, nome, nome_chave, unidade, categoria, status, criado_por, curado_por, curado_em) VALUES
    (@muss, 'Muçarela', 'MUCARELA', 'g', 'Laticínios', 'validado', 1, 1, now()),
    (@catu, 'Catupiry original', 'CATUPIRY ORIGINAL', 'g', 'laticinios', 'validado', 1, 1, now()),
    (@oleo, 'Óleo de soja', 'OLEO DE SOJA', 'un', NULL, 'validado', 1, 1, now()),
    (@cand, 'Requeijão', 'REQUEIJAO', 'g', NULL, 'candidato', 2, NULL, NULL);
INSERT INTO gestao_vinculos (id, estabelecimento_id, nome_chave, descricao, unidade_cupom, insumo_id, global_id, unidade_base, fator, confirmado_por, confirmado_em) VALUES
    (gen_random_uuid(), @l1, 'QJO MUSS', 'QJO MUSS', 'KG', gen_random_uuid(), @muss, 'g', 1000, 2, now() - interval '9 days'),
    (gen_random_uuid(), @l2, 'QJO MUSS', 'QJO MUSS', 'KG', gen_random_uuid(), @muss, 'g', 1000, 2, now() - interval '5 days');",
                    new { muss, catu, oleo, cand, l1, l2 });
            }
            var global = new CuradoriaGlobalService(fonte);
            var curadoria = new CuradoriaDecisoesService(fonte);
            var lojaAntes = await DaLoja(fonte);

            // Categorias que já estavam em uso entram na lista (as duas grafias de Laticínios viram uma só).
            var categorias = await global.Categorias(default);
            var laticinios = Assert.Single(categorias);
            Assert.Equal(2, laticinios.Produtos);
            var frios = (await global.CriarCategoria(1, "Zippy", new SalvarCategoriaRequest("Frios", null, "Separar embutidos"), default)).Categoria;
            Assert.Equal("DUPLICADO", (await Assert.ThrowsAsync<GestaoException>(() =>
                global.CriarCategoria(1, "Zippy", new SalvarCategoriaRequest("FRIOS", null, "de novo"), default))).Codigo);

            // Produto global: nome, categoria e ilustração numa correção só; motivo obrigatório; antes/depois só do que mudou.
            Assert.Equal("MOTIVO_OBRIGATORIO", (await Assert.ThrowsAsync<GestaoException>(() =>
                global.CorrigirProduto(1, "Zippy", muss, new CorrigirProdutoGlobalRequest("Muçarela fatiada", null, null, null, " "), default))).Codigo);
            var corrigido = await global.CorrigirProduto(1, "Zippy", muss,
                new CorrigirProdutoGlobalRequest("Muçarela fatiada", null, "Frios", "queijo", "Nome do fornecedor"), default);
            Assert.Equal(("Muçarela fatiada", "Frios", "queijo"), (corrigido.Produto.Nome, corrigido.Produto.Categoria, corrigido.Produto.Ilustracao));
            Assert.Equal(("global:produto:" + muss, "global", "corrigir"), (corrigido.Registro.CasoId, corrigido.Registro.CasoTipo, corrigido.Registro.Decisao));
            Assert.Equal("Nome: Muçarela · Categoria: Laticínios · Ilustração: —", corrigido.Registro.Anterior);
            Assert.Equal("SEM_MUDANCA", (await Assert.ThrowsAsync<GestaoException>(() =>
                global.CorrigirProduto(1, "Zippy", muss, new CorrigirProdutoGlobalRequest("Muçarela fatiada", null, null, null, "x"), default))).Codigo);
            Assert.Equal("DUPLICADO", (await Assert.ThrowsAsync<GestaoException>(() =>
                global.CorrigirProduto(1, "Zippy", catu, new CorrigirProdutoGlobalRequest("Mucarela fatiada", null, null, null, "x"), default))).Codigo);
            // Unidade só muda se nenhuma loja usa o item.
            Assert.Equal("EM_USO", (await Assert.ThrowsAsync<GestaoException>(() =>
                global.CorrigirProduto(1, "Zippy", muss, new CorrigirProdutoGlobalRequest(null, "ml", null, null, "x"), default))).Codigo);
            Assert.Equal("ml", (await global.CorrigirProduto(2, "Marina", oleo, new CorrigirProdutoGlobalRequest(null, "ml", null, null, "Óleo é em ml"), default)).Produto.Unidade);

            // Kits: só itens validados; corrigir itens e desativar; nada some do histórico.
            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() =>
                global.CriarKit(1, "Zippy", new SalvarKitRequest("Pizzaria", null, new[] { muss, cand }, null, "kit novo"), default))).Codigo);
            var kit = (await global.CriarKit(1, "Zippy", new SalvarKitRequest("Pizzaria", "Básico", new[] { muss, catu }, null, "kit novo"), default)).Kit;
            Assert.Equal(new[] { muss, catu }, kit.Itens.Select(i => i.GlobalId));
            var kitCorrigido = await global.CorrigirKit(2, "Marina", kit.Id, new SalvarKitRequest(null, null, new[] { catu }, false, "Muçarela sai"), default);
            Assert.False(kitCorrigido.Kit.Ativo);
            Assert.Equal(new[] { catu }, kitCorrigido.Kit.Itens.Select(i => i.GlobalId));
            Assert.Contains("Itens: Muçarela fatiada, Catupiry original", kitCorrigido.Registro.Anterior);

            // Renomear categoria muda o global (as duas grafias); desativada não serve para produto, mas quem já tinha continua.
            var renomeada = await global.CorrigirCategoria(1, "Zippy", laticinios.Id, new SalvarCategoriaRequest("Laticínios e queijos", null, "Nome melhor"), default);
            Assert.Equal(1, renomeada.ProdutosAtualizados);
            await global.CorrigirCategoria(1, "Zippy", frios.Id, new SalvarCategoriaRequest(null, false, "Não usar mais"), default);
            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() =>
                global.CorrigirProduto(1, "Zippy", catu, new CorrigirProdutoGlobalRequest(null, null, "Frios", null, "x"), default))).Codigo);
            Assert.Equal("pao_topo", (await global.CorrigirProduto(1, "Zippy", muss, new CorrigirProdutoGlobalRequest(null, null, "Frios", "pao_topo", "x"), default)).Produto.Ilustracao);

            // Critérios: padrão 5/10/10%; mudar grava versão com responsável; vale na próxima decisão.
            var criterios = await global.Criterios(default);
            Assert.Equal((5, 10, 0.1m, (string?)null), (criterios.Atual.Lojas, criterios.Atual.Confirmacoes, criterios.Atual.DivergenciaMax, criterios.Atual.Responsavel));
            Assert.Equal("MOTIVO_OBRIGATORIO", (await Assert.ThrowsAsync<GestaoException>(() =>
                global.SalvarCriterios(1, "Zippy", new SalvarCriteriosRequest(2, 2, 0.1m, null), default))).Codigo);
            criterios = await global.SalvarCriterios(2, "Marina", new SalvarCriteriosRequest(2, 2, 0.1m, "Começo da operação"), default);
            Assert.Equal((2, "Marina"), (criterios.Atual.Lojas, criterios.Atual.Responsavel));
            Assert.Equal("SEM_MUDANCA", (await Assert.ThrowsAsync<GestaoException>(() =>
                global.SalvarCriterios(1, "Zippy", new SalvarCriteriosRequest(2, 2, 0.1000m, "igual"), default))).Codigo);
            var aprovado = await curadoria.Decidir(1, "Zippy", new DecidirCuradoriaRequest("texto:QJO MUSS", "aprovar", null, null, null), default);
            Assert.Equal(CuradoriaDecisoesRegras.MotivoPadraoAprovacao, aprovado.Motivo);
            Assert.Null(aprovado.Excecao);

            // Histórico: correções do global ficam fora da fila e não se reabrem.
            var historico = await global.Historico(null, default);
            Assert.Equal(8, historico.Count);
            Assert.Equal(2, (await global.Historico("global:kit:" + kit.Id, default)).Count);
            Assert.DoesNotContain(await curadoria.Decisoes(default), r => r.CasoTipo == "global");
            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() =>
                curadoria.Reabrir(1, "Zippy", corrigido.Registro.Id, new ReabrirDecisaoRequest(null, "voltar"), default))).Codigo);

            // Regra 6: o vínculo e o insumo de cada loja não mudaram.
            Assert.Equal(lojaAntes, await DaLoja(fonte));

            // Regra 2: nada se apaga nem se reescreve.
            await using var x = await fonte.OpenConnectionAsync();
            await Assert.ThrowsAsync<PostgresException>(() => x.ExecuteAsync("UPDATE gestao_curadoria_decisoes SET motivo = 'x' WHERE caso_tipo = 'global'"));
            await Assert.ThrowsAsync<PostgresException>(() => x.ExecuteAsync("UPDATE gestao_curadoria_criterios SET lojas = 9"));
            await Assert.ThrowsAsync<PostgresException>(() => x.ExecuteAsync("DELETE FROM gestao_curadoria_criterios"));
            await Assert.ThrowsAsync<PostgresException>(() => x.ExecuteAsync("DELETE FROM gestao_catalogo_categorias"));
        }
        finally
        {
            await fonte.DisposeAsync();
            await using var admin = new NpgsqlConnection(conexao); await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }

    private static async Task<string> DaLoja(NpgsqlDataSource fonte)
    {
        await using var c = await fonte.OpenConnectionAsync();
        return string.Join("|", await c.QueryAsync<string>(
            "SELECT estabelecimento_id || ':' || nome_chave || ':' || insumo_id || ':' || global_id || ':' || unidade_base FROM gestao_vinculos ORDER BY 1"));
    }
}
