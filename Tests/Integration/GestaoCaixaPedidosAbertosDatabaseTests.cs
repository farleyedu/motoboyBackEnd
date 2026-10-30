using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Caixa;
using APIBack.Security;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>Pedidos em aberto do Caixa (6C1-C): só leitura da tabela pedido, por loja, com valor e situação reais.</summary>
public class GestaoCaixaPedidosAbertosDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task Lista_so_pedidos_operacionais_da_loja_com_valor_e_situacao_reais()
    {
        var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
        APIBack.Infrastructure.DapperConfiguration.Configure();
        var opcoes = new NpgsqlConnectionStringBuilder(conexao);
        if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
        var schema = "gestao_cxp_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(conexao)) { await admin.OpenAsync(); await admin.ExecuteAsync($"CREATE SCHEMA {schema}"); }
        opcoes.SearchPath = schema;
        var fonte = NpgsqlDataSource.Create(opcoes.ConnectionString);
        try
        {
            var loja = Guid.NewGuid();
            var outra = Guid.NewGuid();
            var merchant = Guid.NewGuid();
            var externo = Guid.NewGuid();
            await using (var c = await fonte.OpenConnectionAsync())
            {
                await c.ExecuteAsync(@"CREATE TABLE estabelecimentos(id uuid PRIMARY KEY, timezone_iana text);
CREATE TABLE usuario(id integer PRIMARY KEY, nome text);
CREATE TABLE pedido(id integer PRIMARY KEY, id_estabelecimento uuid, status_pedido integer, value text, data_pedido text, origem text, canal text);
CREATE TABLE ifood_order_links(merchant_id uuid, external_order_id uuid, estabelecimento_id uuid, pedido_id integer, display_number text);
CREATE TABLE ifood_order_snapshots(merchant_id uuid, external_order_id uuid, estabelecimento_id uuid, order_type text, order_amount numeric, pending_amount numeric);
INSERT INTO estabelecimentos VALUES (@loja, 'America/Sao_Paulo'), (@outra, 'America/Sao_Paulo'); INSERT INTO usuario VALUES (8, 'Júlia do caixa');", new { loja, outra });
                foreach (var m in new[] { "20261105_01_gestao_fundacao.sql", "20261106_01_gestao_documentos.sql", "20261107_01_gestao_vinculos.sql",
                             "20261110_01_gestao_caixa.sql" })
                    await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", m)));
            }
            var livro = new AcontecimentosService(fonte, Array.Empty<IAcontecimentoAssinante>(), NullLogger<AcontecimentosService>.Instance);
            var caixa = new CaixaService(fonte, livro);
            var operador = GestaoPapeis.Resolver((m, _) => m is "Caixa", false);
            var hoje = await caixa.Hoje(loja, default);

            // Caixa ainda não aberto: nada a listar, mesmo com pedido ativo.
            await using (var c = await fonte.OpenConnectionAsync())
                await c.ExecuteAsync("INSERT INTO pedido VALUES (1, @loja, 1, '40.00', @hoje, 'atendente', NULL)", new { loja, hoje = hoje.ToString("yyyy-MM-dd") });
            Assert.Empty((await caixa.CarregarDia(loja, hoje, default))!.PedidosEmAberto);

            await caixa.Abrir(loja, 8, operador, new AbrirCaixaRequest(hoje, 100), default);

            var d = hoje.ToString("yyyy-MM-dd");
            var ontem = hoje.AddDays(-1).ToString("dd/MM/yyyy");
            await using (var c = await fonte.OpenConnectionAsync())
                await c.ExecuteAsync(@"INSERT INTO pedido VALUES
 (2, @loja, 5, '55,50', @d, 'cardapio', NULL),
 (3, @loja, 2, '18.00', @d, 'bot', 'whatsapp'),
 (4, @loja, 8, '22.00', @ontem, 'atendente', 'balcao'),
 (5, @loja, 6, '10.00', @d, 'atendente', NULL),
 (6, @loja, 4, '11.00', @d, 'atendente', NULL),
 (7, @loja, 3, '12.00', @d, 'atendente', NULL),
 (8, @loja, 7, '13.00', @d, 'atendente', NULL),
 (9, @outra, 1, '99.00', @d, 'atendente', NULL),
 (10, @loja, 2, '30.00', @d, 'ifood', NULL);
INSERT INTO ifood_order_links VALUES (@merchant, @externo, @loja, 10, '4821');
INSERT INTO ifood_order_snapshots VALUES (@merchant, @externo, @loja, 'DELIVERY', 87.90, 10.00);",
                    new { loja, outra, d, ontem, merchant, externo });

            var dia = (await caixa.CarregarDia(loja, hoje, default))!;
            var porNumero = dia.PedidosEmAberto.ToDictionary(p => p.Numero);
            // Aparecem: pendente (1), atribuído (2), em rota (3), aguardando motoboy (4, de ontem) e o iFood (#4821). Os demais não.
            Assert.Equal(new[] { "#1", "#2", "#3", "#4", "#4821" }.OrderBy(x => x), porNumero.Keys.OrderBy(x => x));
            Assert.Equal(("pendente", 40m), (porNumero["#1"].Situacao, porNumero["#1"].Valor));
            Assert.Equal(("atribuido", 55.50m, "Cardápio web"), (porNumero["#2"].Situacao, porNumero["#2"].Valor, porNumero["#2"].Onde));
            Assert.Equal(("em_rota", "WhatsApp"), (porNumero["#3"].Situacao, porNumero["#3"].Onde));
            // Pedido de ontem ainda ativo: aparece hoje com a data original.
            Assert.Equal(("aguardando_motoboy", "Balcão", hoje.AddDays(-1).ToString("yyyy-MM-dd")), (porNumero["#4"].Situacao, porNumero["#4"].Onde, porNumero["#4"].Dia));
            // iFood: total oficial do pedido, não o saldo pendente nem o valor local.
            Assert.Equal(("iFood · entrega", 87.90m, "em_rota"), (porNumero["#4821"].Onde, porNumero["#4821"].Valor, porNumero["#4821"].Situacao));

            // Operador só vê o dia de hoje (controller) e não recebe fechamento nem esperado antes da contagem.
            Assert.Null(dia.Fechamento);
            // Pedido concluído sai da lista.
            await using (var c = await fonte.OpenConnectionAsync()) await c.ExecuteAsync("UPDATE pedido SET status_pedido = 3 WHERE id = 3");
            Assert.DoesNotContain((await caixa.CarregarDia(loja, hoje, default))!.PedidosEmAberto, p => p.Numero == "#3");
            // Outra loja nunca vê os pedidos desta.
            Assert.DoesNotContain((await caixa.CarregarDia(loja, hoje, default))!.PedidosEmAberto, p => p.Numero == "#9");
        }
        finally
        {
            await using var admin = new NpgsqlConnection(conexao); await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }

    [Theory]
    [InlineData("2026-10-27 23:59:00", "2026-10-27")]
    [InlineData("28/10/2026", "2026-10-28")]
    [InlineData("2026-10-27", "2026-10-27")]
    [InlineData("lixo", null)]
    [InlineData(null, null)]
    public void Dia_original_le_os_formatos_legados(string? bruto, string? esperado) =>
        Assert.Equal(esperado, PedidosEmAbertoQuery.DiaOriginal(bruto));

    [Theory]
    [InlineData("80.00", 80)]
    [InlineData("1.234,50", 1234.50)]
    [InlineData("R$ 12,90", 12.90)]
    [InlineData("-5", 0)]
    [InlineData("abc", 0)]
    [InlineData(null, 0)]
    public void Valor_legado_vira_decimal(string? texto, double esperado) =>
        Assert.Equal((decimal)esperado, PedidosEmAbertoQuery.Dinheiro(texto));
}
