using System.Text.Json;
using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Caixa;
using APIBack.Security;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// Abrir de novo depois de fechar, contra PostgreSQL de verdade: reabrir desfaz o fechamento (estorno da quebra e do recolhimento,
/// o dinheiro volta para a gaveta) e o dia fecha de novo; novo turno mantém o fechamento, abre com outro fundo, a fita conta só o
/// turno novo e o turno anterior continua na lista de fechamentos. Operador não desfaz fechamento com diferença.
/// </summary>
public class GestaoCaixaTurnosDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task Reabrir_e_novo_turno_no_mesmo_dia()
    {
        var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
        APIBack.Infrastructure.DapperConfiguration.Configure();
        var opcoes = new NpgsqlConnectionStringBuilder(conexao);
        if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
        var schema = "gestao_cxt_" + Guid.NewGuid().ToString("N");
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
CREATE TABLE pedido(id integer PRIMARY KEY, id_estabelecimento uuid, status_pedido integer, value text, data_pedido text, origem text, canal text);
CREATE TABLE ifood_order_links(merchant_id uuid, external_order_id uuid, estabelecimento_id uuid, pedido_id integer, display_number text);
CREATE TABLE ifood_order_snapshots(merchant_id uuid, external_order_id uuid, estabelecimento_id uuid, order_type text, order_amount numeric, pending_amount numeric);
INSERT INTO estabelecimentos VALUES (@loja, 'America/Sao_Paulo'); INSERT INTO usuario VALUES (7, 'Marina'), (8, 'Júlia do caixa');", new { loja });
                foreach (var m in new[] { "20261105_01_gestao_fundacao.sql", "20261106_01_gestao_documentos.sql", "20261107_01_gestao_vinculos.sql",
                             "20261110_01_gestao_caixa.sql", "20261118_01_gestao_caixa_correcoes.sql", "20261123_01_gestao_caixa_aprovacao.sql",
                             "20261125_01_gestao_caixa_turnos.sql", "20261126_01_gestao_caixa_operacao.sql" })
                    await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", m)));
            }
            var livro = new AcontecimentosService(fonte, Array.Empty<IAcontecimentoAssinante>(), NullLogger<AcontecimentosService>.Instance);
            var caixa = new CaixaService(fonte, livro);
            var gerente = GestaoPapeis.Resolver((_, _) => true, false);
            var operador = GestaoPapeis.Resolver((m, _) => m is "Caixa", false);
            var hoje = await caixa.Hoje(loja, default);
            var maq = new Dictionary<string, decimal>();

            // Aberto com R$ 100 e R$ 50 de venda em dinheiro: esperado R$ 150.
            await caixa.Abrir(loja, 8, operador, new AbrirCaixaRequest(hoje, 100), default);
            var gaveta = await Scalar<Guid>(fonte, "SELECT conta_gaveta_id FROM gestao_caixas");
            await livro.Publicar(loja, 7, new PublicarAcontecimentoRequest
            {
                ChaveIdempotencia = "venda:1", Tipo = "venda", Modulo = ModuloGestao.Pedidos, DataFato = DateTimeOffset.UtcNow, Descricao = "Pedido #1",
                Origem = new OrigemRef { Tipo = "pedido", Id = "1" }, Dados = JsonSerializer.SerializeToElement(new { forma = FormaCaixa.Dinheiro }),
                Lancamentos = new() { new LancamentoRequest { Natureza = Natureza.Receita, Valor = 50, ContaId = gaveta, DataPagamento = hoje } },
            }, default);

            // Aberto não reabre nem abre turno.
            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() =>
                caixa.NovoTurno(loja, 8, operador, new NovoTurnoRequest(hoje, 100), default))).Codigo);

            // Gerente fecha contando R$ 140 (faltou R$ 10), deixa R$ 100 de troco: R$ 40 vão para o cofre.
            var contagem = new ContagemCaixa(new Dictionary<int, int> { [100] = 1, [20] = 2 }, 0);
            var dia = await caixa.Fechar(loja, 7, gerente, new FecharCaixaRequest(hoje, contagem, maq, "Troco errado", 100), default);
            Assert.False(dia.Aberto);
            Assert.Equal(100m, await Saldo(livro, loja, gaveta));

            // Operador não desfaz fechamento com diferença; sem motivo ninguém desfaz.
            Assert.Equal("SEM_PERMISSAO", (await Assert.ThrowsAsync<GestaoException>(() =>
                caixa.Reabrir(loja, 8, operador, new ReabrirCaixaRequest(hoje, "Fechei cedo"), default))).Codigo);
            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() =>
                caixa.Reabrir(loja, 7, gerente, new ReabrirCaixaRequest(hoje, " "), default))).Codigo);

            // Gerente reabre: quebra e recolhimento estornados, a gaveta volta a R$ 150 e a fita continua do mesmo jeito.
            dia = await caixa.Reabrir(loja, 7, gerente, new ReabrirCaixaRequest(hoje, "Chegou mais pedido"), default);
            Assert.True(dia.Aberto);
            Assert.Null(dia.Fechamento);
            Assert.Equal(1, dia.Turno);
            Assert.Equal(150m, await Saldo(livro, loja, gaveta));
            Assert.Equal(150m, dia.Fita.Sum(l => l.Valor));
            Assert.Equal(("reaberto", "Chegou mais pedido"), (dia.Anteriores![0].Destino, dia.Anteriores[0].Motivo));
            Assert.Equal(2, await Scalar<int>(fonte, "SELECT count(*) FROM gestao_acontecimentos WHERE correcao = 'estorno'"));
            Assert.Empty(await caixa.Fechamentos(loja, new DateOnly(hoje.Year, hoje.Month, 1), default));

            // Fecha de novo, agora batendo (chaves do livro separadas pela versão), e abre o 2º turno com R$ 80 de fundo.
            dia = await caixa.Fechar(loja, 7, gerente, new FecharCaixaRequest(hoje, new ContagemCaixa(new Dictionary<int, int> { [100] = 1, [50] = 1 }, 0), maq, null, 100), default);
            Assert.Equal(150m, dia.Fechamento!.Contado);
            Assert.Empty(dia.Fechamento.Ajustes);
            dia = await caixa.NovoTurno(loja, 8, operador, new NovoTurnoRequest(hoje, 80), default);
            Assert.True(dia.Aberto);
            Assert.Equal((2, 80m), (dia.Turno, dia.Fundo));
            Assert.Equal(80m, await Saldo(livro, loja, gaveta));
            Assert.Equal(new[] { ("abre", 80m) }, dia.Fita.Select(l => (l.Tipo, l.Valor)));
            Assert.Equal(new[] { "reaberto", "novo_turno" }, dia.Anteriores!.Select(a => a.Destino));

            // O 1º turno continua nos fechamentos (sem ajuste); o 2º fecha e entra também.
            dia = await caixa.Fechar(loja, 8, operador, new FecharCaixaRequest(hoje, new ContagemCaixa(new Dictionary<int, int> { [50] = 1, [20] = 1, [10] = 1 }, 0), maq, null, 80), default);
            var fechamentos = await caixa.Fechamentos(loja, new DateOnly(hoje.Year, hoje.Month, 1), default);
            Assert.Equal(new[] { (2, true), (1, false) }, fechamentos.Select(f => (f.Turno, f.Ajustavel)));

            // O operador fechou o 2º turno (dentro da tolerância): fica esperando o gerente e nenhum caixa novo abre antes disso.
            Assert.True(dia.AprovacaoPendente);
            var operacao = await caixa.Operacao(loja, default);
            Assert.Equal((hoje, "fechado", 2), (operacao.Dia, operacao.Situacao, operacao.Turno));
            Assert.Equal("05:00", operacao.ViradaDoDia);
            Assert.True(Assert.Single(operacao.Pendentes).Fechado);
            Assert.Equal("SEM_APROVACAO", (await Assert.ThrowsAsync<GestaoException>(() =>
                caixa.NovoTurno(loja, 8, operador, new NovoTurnoRequest(hoje, 80), default))).Codigo);
            Assert.Equal("SEM_PERMISSAO", (await Assert.ThrowsAsync<GestaoException>(() =>
                caixa.DecidirFechamento(loja, 8, operador, hoje, new DecisaoDoFechamentoRequest("aprovar", null, null, null, null), default))).Codigo);

            // O gerente aprova: o visto não mexe no livro e libera o próximo caixa.
            dia = await caixa.DecidirFechamento(loja, 7, gerente, hoje, new DecisaoDoFechamentoRequest("aprovar", null, null, null, null), default);
            Assert.False(dia.AprovacaoPendente);
            Assert.Empty((await caixa.Operacao(loja, default)).Pendentes);
            dia = await caixa.NovoTurno(loja, 8, operador, new NovoTurnoRequest(hoje, 80), default);
            Assert.Equal((3, true), (dia.Turno, dia.Aberto));
            Assert.Equal($"{hoje:yyyy-MM-dd}:t1", fechamentos[1].Id);

            await using (var c = await fonte.OpenConnectionAsync())
                Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() => c.ExecuteAsync("DELETE FROM gestao_caixa_turnos"))).SqlState);
        }
        finally
        {
            await fonte.DisposeAsync();
            await using var admin = new NpgsqlConnection(conexao); await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }

    private static async Task<decimal> Saldo(IAcontecimentosService livro, Guid loja, Guid conta) =>
        (await livro.Contas(loja, default)).First(x => x.Id == conta).Saldo;

    private static async Task<T> Scalar<T>(NpgsqlDataSource fonte, string sql)
    {
        await using var c = await fonte.OpenConnectionAsync();
        return (await c.ExecuteScalarAsync<T>(sql))!;
    }
}
