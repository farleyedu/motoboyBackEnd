using APIBack.Acontecimentos;
using APIBack.DTOs.Configuracoes;
using APIBack.GestaoLogica.Caixa;
using APIBack.Repository.Interface;
using APIBack.Security;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// O caixa só abre com a loja aberta operacionalmente (o estado oficial de <c>IHorarioOperacaoRepository</c>): primeira abertura,
/// novo turno e reabertura recusam com LOJA_FECHADA; a consulta do dia e a tela de operação continuam respondendo e dizem por quê.
/// </summary>
public class GestaoCaixaLojaFechadaDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task Caixa_nao_abre_nem_reabre_com_a_loja_fechada()
    {
        var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
        APIBack.Infrastructure.DapperConfiguration.Configure();
        var opcoes = new NpgsqlConnectionStringBuilder(conexao);
        if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
        var schema = "gestao_cxl_" + Guid.NewGuid().ToString("N");
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
            var estado = new StoreOperationDto
            {
                EstabelecimentoId = loja, AbertoAgora = false, DentroDoHorario = false, MotivoFechada = "FORA_HORARIO", ProximaAbertura = "hoje às 18:00",
            };
            var horarios = new Mock<IHorarioOperacaoRepository>();
            horarios.Setup(h => h.ObterEstadoAsync(loja, It.IsAny<CancellationToken>())).ReturnsAsync(() => estado);
            var caixa = new CaixaService(fonte, livro, horarios: horarios.Object);
            var gerente = GestaoPapeis.Resolver((_, _) => true, false);
            var hoje = await caixa.Hoje(loja, default);

            // Loja fechada: não abre, não cria o dia, mas a tela de operação responde e orienta.
            var recusa = await Assert.ThrowsAsync<GestaoException>(() => caixa.Abrir(loja, 7, gerente, new AbrirCaixaRequest(hoje, 100), default));
            Assert.Equal(CaixaService.LojaFechada, recusa.Codigo);
            Assert.Equal(409, recusa.Status);
            Assert.Contains("abre hoje às 18:00", recusa.Message);
            Assert.False((await caixa.CarregarDia(loja, hoje, default))!.Aberto);
            var operacao = await caixa.Operacao(loja, default);
            Assert.Equal("sem_caixa", operacao.Situacao);
            Assert.False(operacao.LojaAberta);
            Assert.Equal("FORA_HORARIO", operacao.MotivoLojaFechada);

            // Loja aberta: abre normalmente. Depois que o caixa já existe, repetir a abertura não depende mais da loja.
            estado = new StoreOperationDto { EstabelecimentoId = loja, AbertoAgora = true, DentroDoHorario = true };
            Assert.True((await caixa.Abrir(loja, 7, gerente, new AbrirCaixaRequest(hoje, 100), default)).Aberto);
            Assert.True((await caixa.Operacao(loja, default)).LojaAberta);

            // Fecha o caixa e fecha a loja: novo turno e reabertura recusam; fechar a loja não derruba o caixa que já está aberto.
            var maq = new Dictionary<string, decimal>();
            var contagem = new ContagemCaixa(new Dictionary<int, int> { [100] = 1 }, 0);
            Assert.False((await caixa.Fechar(loja, 7, gerente, new FecharCaixaRequest(hoje, contagem, maq, null, 100), default)).Aberto);
            estado = new StoreOperationDto { EstabelecimentoId = loja, AbertoAgora = false, MotivoFechada = "PAUSADA" };
            Assert.Equal(CaixaService.LojaFechada, (await Assert.ThrowsAsync<GestaoException>(() =>
                caixa.NovoTurno(loja, 7, gerente, new NovoTurnoRequest(hoje, 100), default))).Codigo);
            Assert.Equal(CaixaService.LojaFechada, (await Assert.ThrowsAsync<GestaoException>(() =>
                caixa.Reabrir(loja, 7, gerente, new ReabrirCaixaRequest(hoje, "Esqueci um pedido"), default))).Codigo);
            Assert.False((await caixa.CarregarDia(loja, hoje, default))!.Aberto);

            // Loja aberta de novo: reabre.
            estado = new StoreOperationDto { EstabelecimentoId = loja, AbertoAgora = true };
            Assert.True((await caixa.Reabrir(loja, 7, gerente, new ReabrirCaixaRequest(hoje, "Esqueci um pedido"), default)).Aberto);
        }
        finally
        {
            await using var admin = new NpgsqlConnection(conexao);
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }
}
