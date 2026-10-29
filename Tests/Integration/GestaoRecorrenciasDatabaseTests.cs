using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Despesas;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// Contas fixas contra PostgreSQL de verdade: gerar o mês (fixo publica, variável aguarda), não lançar duas vezes, lançar o valor da
/// conta variável, estorno e nova geração com outro valor, arquivar sem apagar, lembretes e finalidades da loja.
/// </summary>
public class GestaoRecorrenciasDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task Gera_o_mes_lanca_variavel_estorna_arquiva_e_lembra()
    {
        var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
        APIBack.Infrastructure.DapperConfiguration.Configure();
        var opcoes = new NpgsqlConnectionStringBuilder(conexao);
        if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
        var schema = "gestao_rec_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(conexao)) { await admin.OpenAsync(); await admin.ExecuteAsync($"CREATE SCHEMA {schema}"); }
        opcoes.SearchPath = schema;
        var fonte = NpgsqlDataSource.Create(opcoes.ConnectionString);
        try
        {
            var loja = Guid.NewGuid();
            await using (var c = await fonte.OpenConnectionAsync())
            {
                await c.ExecuteAsync(@"CREATE TABLE estabelecimentos(id uuid PRIMARY KEY, timezone_iana text);
CREATE TABLE usuario(id integer PRIMARY KEY, nome text);
INSERT INTO estabelecimentos VALUES (@loja, 'America/Sao_Paulo'); INSERT INTO usuario VALUES (7, 'Marina');", new { loja });
                foreach (var m in new[] { "20261105_01_gestao_fundacao.sql", "20261106_01_gestao_documentos.sql", "20261107_01_gestao_vinculos.sql",
                             "20261111_01_gestao_recorrencias.sql", "20261111_01_gestao_recorrencias.sql" })
                    await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", m)));
            }
            var livro = new AcontecimentosService(fonte, Array.Empty<IAcontecimentoAssinante>(), NullLogger<AcontecimentosService>.Instance);
            var rec = new RecorrenciasService(fonte, livro);
            var hoje = AcontecimentoRegras.DiaLocal(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"));
            var mes = RecorrenciaRegras.Mes(hoje);

            var aluguel = await rec.Salvar(loja, 7, null, new ContaFixaRequest("Aluguel do salão", "Aluguel", 2500, 10), default);
            var luz = await rec.Salvar(loja, 7, null, new ContaFixaRequest("Luz", "Energia", 300, 20, ValorVaria: true), default);
            var internet = await rec.Salvar(loja, 7, null, new ContaFixaRequest("Internet", "Internet", 120, 31), default);

            var antes = await rec.Ocorrencias(loja, mes, default);
            Assert.Equal(new[] { (aluguel.Id, EstadoOcorrencia.Prevista), (internet.Id, EstadoOcorrencia.Prevista), (luz.Id, EstadoOcorrencia.AguardandoValor) },
                antes.Select(o => (o.ContaFixaId, o.Estado)));
            Assert.Empty(await rec.Ocorrencias(loja, mes.AddMonths(-1), default)); // antes de a conta existir

            // Gerar o mês: o fixo vira despesa a pagar com vencimento; a luz aguarda o valor. Gerar de novo não duplica.
            var g = await rec.Gerar(loja, 7, mes, default);
            Assert.Equal(new[] { 2500m, 120m }, g.Geradas.Select(o => o.Valor));
            Assert.Equal(luz.Id, Assert.Single(g.AguardandoValor).ContaFixaId);
            Assert.Empty((await rec.Gerar(loja, 7, mes, default)).Geradas);
            Assert.Equal(new DateOnly(mes.Year, mes.Month, 10),
                await Scalar<DateTime>(fonte, "SELECT data_vencimento FROM gestao_lancamentos WHERE valor = 2500").ContinueWith(t => DateOnly.FromDateTime(t.Result)));
            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() => rec.Gerar(loja, 7, mes.AddMonths(2), default))).Codigo);

            // Conta da luz chegou: R$ 287,45. Lançar de novo no mesmo mês não pode.
            var luzLancada = await rec.Lancar(loja, 7, luz.Id, new LancarOcorrenciaRequest("luz-1", mes, 287.45m), default);
            Assert.Equal((EstadoOcorrencia.Lancada, StatusLancamento.APagar), (luzLancada.Estado, luzLancada.Status));
            Assert.Equal("DUPLICADO", (await Assert.ThrowsAsync<GestaoException>(() =>
                rec.Lancar(loja, 7, luz.Id, new LancarOcorrenciaRequest("luz-2", mes, 290m), default))).Codigo);

            // Aluguel subiu e o lançamento do mês estava errado: estorno; gerar de novo usa o valor novo (origem ~1).
            Assert.Equal("IDEMPOTENCIA_CONFLITO", (await Assert.ThrowsAsync<GestaoException>(() =>
                rec.Salvar(loja, 7, aluguel.Id, new ContaFixaRequest("Aluguel do salão", "Aluguel", 2600, 10, Versao: 9), default))).Codigo);
            await rec.Salvar(loja, 7, aluguel.Id, new ContaFixaRequest("Aluguel do salão", "Aluguel", 2600, 10, Versao: aluguel.Versao), default);
            var aluguelDoMes = g.Geradas.Single(o => o.ContaFixaId == aluguel.Id).AcontecimentoId!.Value;
            await livro.Estornar(loja, 7, aluguelDoMes, new EstornoRequest { ChaveIdempotencia = "est-aluguel", Motivo = "Valor antigo", ConfirmacaoHumana = true }, default);
            Assert.Equal(EstadoOcorrencia.Prevista, (await rec.Ocorrencias(loja, mes, default)).Single(o => o.ContaFixaId == aluguel.Id).Estado);
            Assert.Equal(2600m, Assert.Single((await rec.Gerar(loja, 7, mes, default)).Geradas).Valor);
            Assert.Equal(1, await Scalar<int>(fonte, "SELECT count(*) FROM gestao_acontecimentos WHERE origem_id LIKE '%~1'"));

            // Arquivar: sai da lista e dos meses seguintes, mas o lançamento deste mês continua.
            await rec.Arquivar(loja, 7, internet.Id, default);
            Assert.DoesNotContain(await rec.Listar(loja, false, default), f => f.Id == internet.Id);
            Assert.Contains(await rec.Ocorrencias(loja, mes, default), o => o.ContaFixaId == internet.Id && o.Estado == EstadoOcorrencia.Lancada);
            Assert.DoesNotContain(await rec.Ocorrencias(loja, mes.AddMonths(1), default), o => o.ContaFixaId == internet.Id);

            // Lembretes: nada pago aparece; a luz paga sai da lista.
            var banco = (await livro.CriarConta(loja, 7, new ContaRequest { ChaveIdempotencia = "banco", Tipo = TipoConta.Banco, Nome = "Inter" }, default)).Id;
            var lembretes = await rec.Lembretes(loja, 40, default);
            var luzLembrete = Assert.Single(lembretes, l => l.ContaFixaId == luz.Id && l.Mes == mes);
            Assert.NotNull(luzLembrete.LancamentoId);
            Assert.All(lembretes, l => Assert.True(l.DiasParaVencer <= 40));
            await livro.Liquidar(loja, 7, new LiquidarRequest { ChaveIdempotencia = "paga-luz", LancamentoId = luzLembrete.LancamentoId!.Value, ContaId = banco, DataPagamento = hoje }, default);
            Assert.DoesNotContain(await rec.Lembretes(loja, 40, default), l => l.ContaFixaId == luz.Id && l.Mes == mes);

            // O job gera sozinho a conta fixa nova do mês; rodar de novo não duplica (Integração 6A).
            await rec.Salvar(loja, 7, null, new ContaFixaRequest("Gás de cozinha", "Gás", 90, 5), default);
            Assert.Equal(1, await ContasFixasWorker.GerarTodas(fonte, rec, hoje, NullLogger.Instance, default));
            Assert.Equal(0, await ContasFixasWorker.GerarTodas(fonte, rec, hoje, NullLogger.Instance, default));
            Assert.Equal(EstadoOcorrencia.Lancada, (await rec.Ocorrencias(loja, mes, default)).Single(o => o.Nome == "Gás de cozinha").Estado);

            // Finalidades da loja: "gás" e "Gás" são a mesma.
            Assert.Equal("Gás", await rec.CriarFinalidade(loja, 7, "Gás", default));
            Assert.Equal("Gás", await rec.CriarFinalidade(loja, 7, " gas ", default));
            Assert.Equal(new[] { "Gás" }, await rec.Finalidades(loja, default));
            await using (var c = await fonte.OpenConnectionAsync())
                Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() => c.ExecuteAsync("DELETE FROM gestao_contas_fixas"))).SqlState);
        }
        finally
        {
            await fonte.DisposeAsync();
            await using var admin = new NpgsqlConnection(conexao); await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }

    private static async Task<T> Scalar<T>(NpgsqlDataSource fonte, string sql)
    {
        await using var c = await fonte.OpenConnectionAsync();
        return (await c.ExecuteScalarAsync<T>(sql))!;
    }
}
