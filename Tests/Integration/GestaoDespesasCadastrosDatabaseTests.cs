using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Despesas;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// L1 da auditoria de edição contra PostgreSQL de verdade: conta fixa editada (histórico, "vale a partir de", o livro não muda),
/// arquivada e reativada; equipamentos no servidor com a migração do navegador sem duplicar; finalidades com id (renomear, desativar,
/// reativar, juntar), sem reescrever o livro.
/// </summary>
public class GestaoDespesasCadastrosDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task Conta_fixa_editada_e_reativada_com_historico()
    {
        await using var b = await Banco.Criar();
        var (livro, rec) = (b.Livro, new RecorrenciasService(b.Fonte, b.Livro));
        var mes = RecorrenciaRegras.Mes(b.Hoje);

        var aluguel = await rec.Salvar(b.Loja, 7, null, new ContaFixaRequest("Aluguel", "Aluguel", 2500, 10), default);
        var internet = await rec.Salvar(b.Loja, 7, null, new ContaFixaRequest("Internet", "Internet", 120, 5), default);
        var g = await rec.Gerar(b.Loja, 7, mes, default);
        Assert.Equal(2, g.Geradas.Count);

        // Editar depois de lançado: o lançamento do mês continua 2500; a mudança vale do mês que vem.
        var editado = await rec.Salvar(b.Loja, 7, aluguel.Id, new ContaFixaRequest("Aluguel do salão", "Aluguel", 2700, 12, Versao: aluguel.Versao,
            Motivo: "Reajuste do contrato"), default);
        Assert.Equal((2700m, 12, aluguel.Versao + 1), (editado.ValorPrevisto, editado.DiaVencimento, editado.Versao));
        Assert.Equal(2500m, (await rec.Ocorrencias(b.Loja, mes, default)).Single(o => o.ContaFixaId == aluguel.Id).Valor);
        Assert.Equal(2700m, (await rec.Ocorrencias(b.Loja, mes.AddMonths(1), default)).Single(o => o.ContaFixaId == aluguel.Id).Valor);
        Assert.Equal(2500m, await Banco.Scalar<decimal>(b.Fonte, "SELECT valor FROM gestao_lancamentos WHERE descricao LIKE 'Aluguel ·%'"));

        // Editar com a versão velha: conflito, nada muda. Salvar igual não vira histórico.
        Assert.Equal("IDEMPOTENCIA_CONFLITO", (await Assert.ThrowsAsync<GestaoException>(() => rec.Salvar(b.Loja, 7, aluguel.Id,
            new ContaFixaRequest("Aluguel", "Aluguel", 1, 1, Versao: aluguel.Versao), default))).Codigo);
        await rec.Salvar(b.Loja, 7, aluguel.Id, new ContaFixaRequest("Aluguel do salão", "Aluguel", 2700, 12), default);

        var h = await rec.Historico(b.Loja, aluguel.Id, default);
        Assert.Equal(new[] { AcaoContaFixa.Editada, AcaoContaFixa.Criada }, h.Select(x => x.Acao));
        Assert.Equal((2500m, 2700m, "Reajuste do contrato", "Marina"), (h[0].Antes!.ValorPrevisto, h[0].Depois.ValorPrevisto, h[0].Motivo, h[0].PorNome));
        Assert.Equal(mes.AddMonths(1), h[0].ValeAPartir); // o mês atual já estava lançado
        Assert.Equal(mes, h[1].ValeAPartir);

        // Arquivar e reativar: volta a lembrar, mas só do mês da reativação; arquivar duas vezes não duplica o histórico.
        await rec.Arquivar(b.Loja, 7, internet.Id, default);
        await rec.Arquivar(b.Loja, 7, internet.Id, default);
        Assert.DoesNotContain(await rec.Listar(b.Loja, false, default), f => f.Id == internet.Id);
        var volta = await rec.Reativar(b.Loja, 7, internet.Id, "Voltamos a contratar", default);
        Assert.False(volta.Arquivada);
        Assert.Contains(await rec.Listar(b.Loja, false, default), f => f.Id == internet.Id);
        Assert.DoesNotContain(await rec.Ocorrencias(b.Loja, mes.AddMonths(-1), default), o => o.ContaFixaId == internet.Id);
        Assert.Equal(new[] { AcaoContaFixa.Reativada, AcaoContaFixa.Arquivada, AcaoContaFixa.Criada },
            (await rec.Historico(b.Loja, internet.Id, default)).Select(x => x.Acao));

        // O histórico não se apaga nem se reescreve.
        await using var c = await b.Fonte.OpenConnectionAsync();
        Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() => c.ExecuteAsync("UPDATE gestao_contas_fixas_historico SET motivo = 'x'"))).SqlState);
        Assert.Equal("NAO_ENCONTRADO", (await Assert.ThrowsAsync<GestaoException>(() => rec.Reativar(b.Loja, 7, Guid.NewGuid(), null, default))).Codigo);
    }

    [DeliveryDatabaseFact]
    public async Task Equipamentos_no_servidor_e_migracao_do_navegador_sem_duplicar()
    {
        await using var b = await Banco.Criar();
        var eq = new EquipamentosService(b.Fonte);

        var freezer = await eq.Criar(b.Loja, 7, new EquipamentoRequest(null, "Freezer", "Metalfrio", "frio", "#38BDF8", "funcionando", "2023-03", 4200), default);
        Assert.StartsWith("equip-", freezer.Id);
        Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() =>
            eq.Criar(b.Loja, 7, new EquipamentoRequest(null, "Chapa", "", "fogo", "#F97316", "quebrado"), default))).Codigo);

        // O navegador tinha dois (um arquivado) e um já igual ao do servidor: entram os dois novos, com o mesmo id.
        var doNavegador = new ImportarEquipamentosRequest(new[]
        {
            new EquipamentoRequest("equip-local-1", "Chapa", "", "fogo", "#F97316", "revisao"),
            new EquipamentoRequest("equip-local-2", "Coifa velha", "", "outro", "#64748B", "parado", Arquivado: true),
            new EquipamentoRequest(freezer.Id, "Freezer (antigo)", "", "frio", "#38BDF8", "parado"),
            new EquipamentoRequest(null, "Sem id", "", "outro", "#64748B", "parado"),
        });
        var r1 = await eq.Importar(b.Loja, 7, doNavegador, default);
        Assert.Equal((2, 1), (r1.Importados, r1.JaExistiam));
        Assert.Equal(new[] { "Sem id" }, r1.Recusados);
        var r2 = await eq.Importar(b.Loja, 7, doNavegador, default);
        Assert.Equal((0, 3), (r2.Importados, r2.JaExistiam));
        Assert.Equal("Freezer", (await eq.Listar(b.Loja, false, default)).Single(e => e.Id == freezer.Id).Nome); // não sobrescreve
        Assert.Equal(new[] { "Chapa", "Freezer" }, (await eq.Listar(b.Loja, false, default)).Select(e => e.Nome));
        Assert.Equal(3, (await eq.Listar(b.Loja, true, default)).Count);

        // Editar com versão, arquivar e reativar.
        var chapa = await eq.Atualizar(b.Loja, 7, "equip-local-1", new EquipamentoRequest(null, "Chapa 2 bocas", "Venâncio", "fogo", "#F97316", "funcionando", Versao: 1), default);
        Assert.Equal(("Chapa 2 bocas", 2), (chapa.Nome, chapa.Versao));
        Assert.Equal("IDEMPOTENCIA_CONFLITO", (await Assert.ThrowsAsync<GestaoException>(() =>
            eq.Atualizar(b.Loja, 7, "equip-local-1", new EquipamentoRequest(null, "Chapa", "", "fogo", "#F97316", "parado", Versao: 1), default))).Codigo);
        Assert.True((await eq.Arquivar(b.Loja, 7, "equip-local-1", true, default)).Arquivado);
        Assert.False((await eq.Arquivar(b.Loja, 7, "equip-local-2", false, default)).Arquivado);
        Assert.Equal("NAO_ENCONTRADO", (await Assert.ThrowsAsync<GestaoException>(() => eq.Arquivar(b.Loja, 7, "equip-nada", true, default))).Codigo);

        // Outra loja não vê.
        Assert.Empty(await eq.Listar(Guid.NewGuid(), true, default));
        await using var c = await b.Fonte.OpenConnectionAsync();
        Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() => c.ExecuteAsync("DELETE FROM gestao_equipamentos"))).SqlState);
    }

    [DeliveryDatabaseFact]
    public async Task Finalidades_com_id_renomeia_desativa_e_junta_sem_reescrever_o_livro()
    {
        await using var b = await Banco.Criar();
        var fin = new FinalidadesService(b.Fonte);

        // Cadastro antigo (só a tabela de texto) entra no cadastro com id na primeira leitura, uma vez.
        await using (var c = await b.Fonte.OpenConnectionAsync())
            await c.ExecuteAsync("INSERT INTO gestao_finalidades (estabelecimento_id, nome, chave, criado_por) VALUES (@loja, 'Gas', 'GAS', 7)", new { loja = b.Loja });
        Assert.Equal(new[] { "Gas" }, await fin.Ativas(b.Loja, default));
        Assert.Single(await fin.Listar(b.Loja, default));
        var gas = (await fin.Listar(b.Loja, default)).Single();

        // Um lançamento antigo com o texto "Gas" no livro.
        await b.Livro.Publicar(b.Loja, 7, new PublicarAcontecimentoRequest
        {
            ChaveIdempotencia = "gas-1", Tipo = "despesa_variavel", Modulo = ModuloGestao.Despesas, DataFato = DateTimeOffset.UtcNow, Descricao = "Botijão",
            Lancamentos = new() { new LancamentoRequest { Natureza = Natureza.Despesa, Valor = 120, Status = StatusLancamento.APagar, MesReferencia = RecorrenciaRegras.Mes(b.Hoje),
                DataVencimento = b.Hoje, Categoria = "Despesas variáveis", Finalidade = "Gas", Descricao = "Botijão" } },
        }, default);

        // Renomear: o id fica, o nome antigo continua ligado a ele; o livro não muda.
        var renomeada = await fin.Renomear(b.Loja, 7, gas.Id, new RenomearFinalidadeRequest("Gás de cozinha", gas.Versao, "Erro de digitação"), default);
        Assert.Equal(("Gás de cozinha", gas.Id), (renomeada.Nome, renomeada.Id));
        Assert.Equal(new[] { "Gas", "Gás de cozinha" }, renomeada.Nomes);
        Assert.Equal("Gas", await Banco.Scalar<string>(b.Fonte, "SELECT finalidade FROM gestao_lancamentos WHERE descricao = 'Botijão'"));
        // Escrever o nome antigo de novo devolve a de hoje; criar outra com o mesmo nome não pode.
        Assert.Equal("Gás de cozinha", await fin.Criar(b.Loja, 7, "gás", default));
        var frete = await fin.Criar(b.Loja, 7, "Frete moto", default);
        var freteId = (await fin.Listar(b.Loja, default)).Single(f => f.Nome == frete).Id;
        Assert.Equal("FINALIDADE_EXISTE", (await Assert.ThrowsAsync<GestaoException>(() =>
            fin.Renomear(b.Loja, 7, freteId, new RenomearFinalidadeRequest("GAS"), default))).Codigo);

        // Desativar sai das opções; escolher o nome de novo reativa.
        await fin.Desativar(b.Loja, 7, freteId, "Não usamos mais", default);
        Assert.DoesNotContain("Frete moto", await fin.Ativas(b.Loja, default));
        Assert.Equal("Frete moto", await fin.Criar(b.Loja, 7, "frete moto", default));
        Assert.Contains("Frete moto", await fin.Ativas(b.Loja, default));

        // Juntar "Frete moto" em "Gás de cozinha" (exemplo): os nomes de Frete passam a mostrar a que ficou. Pede motivo.
        Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() =>
            fin.Juntar(b.Loja, 7, freteId, new JuntarFinalidadeRequest(gas.Id, ""), default))).Codigo);
        await fin.Juntar(b.Loja, 7, freteId, new JuntarFinalidadeRequest(gas.Id, "Era a mesma coisa"), default);
        var lista = await fin.Listar(b.Loja, default);
        Assert.Equal(gas.Id, lista.Single(f => f.Id == freteId).JuntadaEm);
        Assert.Equal(new[] { "Gás de cozinha" }, await fin.Ativas(b.Loja, default));
        Assert.Equal("Gás de cozinha", await fin.Criar(b.Loja, 7, "Frete moto", default));
        Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() => fin.Reativar(b.Loja, 7, freteId, null, default))).Codigo);

        Assert.Equal(new[] { AcaoFinalidade.Juntada, AcaoFinalidade.Reativada, AcaoFinalidade.Desativada, AcaoFinalidade.Criada },
            (await fin.Historico(b.Loja, freteId, default)).Select(h => h.Acao));
        var hGas = await fin.Historico(b.Loja, gas.Id, default);
        Assert.Equal(new[] { AcaoFinalidade.Recebeu, AcaoFinalidade.Renomeada, AcaoFinalidade.Criada }, hGas.Select(h => h.Acao));
        Assert.Equal(("Gas", "Gás de cozinha", "Erro de digitação"), (hGas[1].Antes!.Nome, hGas[1].Depois.Nome, hGas[1].Motivo));

        await using var c2 = await b.Fonte.OpenConnectionAsync();
        Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() => c2.ExecuteAsync("DELETE FROM gestao_finalidades_nomes"))).SqlState);
        Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() => c2.ExecuteAsync("DELETE FROM gestao_finalidades_cadastro"))).SqlState);
    }

    private sealed class Banco : IAsyncDisposable
    {
        public required NpgsqlDataSource Fonte { get; init; }
        public required AcontecimentosService Livro { get; init; }
        public required Guid Loja { get; init; }
        public required DateOnly Hoje { get; init; }
        private string _conexao = "", _schema = "";

        public static async Task<Banco> Criar()
        {
            var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
            APIBack.Infrastructure.DapperConfiguration.Configure();
            var opcoes = new NpgsqlConnectionStringBuilder(conexao);
            if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
            var schema = "gestao_l1_" + Guid.NewGuid().ToString("N");
            await using (var admin = new NpgsqlConnection(conexao)) { await admin.OpenAsync(); await admin.ExecuteAsync($"CREATE SCHEMA {schema}"); }
            opcoes.SearchPath = schema;
            var fonte = NpgsqlDataSource.Create(opcoes.ConnectionString);
            var loja = Guid.NewGuid();
            await using (var c = await fonte.OpenConnectionAsync())
            {
                await c.ExecuteAsync(@"CREATE TABLE estabelecimentos(id uuid PRIMARY KEY, timezone_iana text);
CREATE TABLE usuario(id integer PRIMARY KEY, nome text);
INSERT INTO estabelecimentos VALUES (@loja, 'America/Sao_Paulo'); INSERT INTO usuario VALUES (7, 'Marina');", new { loja });
                // A migração nova duas vezes: é idempotente.
                foreach (var m in new[] { "20261105_01_gestao_fundacao.sql", "20261106_01_gestao_documentos.sql", "20261107_01_gestao_vinculos.sql",
                             "20261111_01_gestao_recorrencias.sql", "20261115_10_gestao_despesas_cadastros.sql", "20261115_10_gestao_despesas_cadastros.sql" })
                    await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", m)));
            }
            return new Banco
            {
                Fonte = fonte, Loja = loja, _conexao = conexao, _schema = schema,
                Livro = new AcontecimentosService(fonte, Array.Empty<IAcontecimentoAssinante>(), NullLogger<AcontecimentosService>.Instance),
                Hoje = AcontecimentoRegras.DiaLocal(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")),
            };
        }

        public static async Task<T> Scalar<T>(NpgsqlDataSource fonte, string sql)
        {
            await using var c = await fonte.OpenConnectionAsync();
            return (await c.ExecuteScalarAsync<T>(sql))!;
        }

        public async ValueTask DisposeAsync()
        {
            await Fonte.DisposeAsync();
            await using var admin = new NpgsqlConnection(_conexao); await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {_schema} CASCADE");
        }
    }
}
