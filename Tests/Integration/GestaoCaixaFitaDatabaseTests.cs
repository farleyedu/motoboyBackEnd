using APIBack.Acontecimentos;
using System.Text.Json;
using APIBack.GestaoLogica.Caixa;
using APIBack.Security;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// A fita liga cada linha de venda ao pedido pelo vínculo do livro (nunca pelo texto) e diz para onde o dinheiro foi: venda dividida
/// (dinheiro + Pix) tem uma linha na gaveta e outra na maquininha, e só a da gaveta é dinheiro físico.
/// </summary>
public class GestaoCaixaFitaDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task Fita_liga_a_venda_ao_pedido_e_separa_gaveta_de_maquininha()
    {
        var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
        APIBack.Infrastructure.DapperConfiguration.Configure();
        var opcoes = new NpgsqlConnectionStringBuilder(conexao);
        if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
        var schema = "gestao_cxf_" + Guid.NewGuid().ToString("N");
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
INSERT INTO estabelecimentos VALUES (@loja, 'America/Sao_Paulo'); INSERT INTO usuario VALUES (7, 'Marina');", new { loja });
                foreach (var m in new[] { "20261105_01_gestao_fundacao.sql", "20261106_01_gestao_documentos.sql", "20261107_01_gestao_vinculos.sql",
                             "20261110_01_gestao_caixa.sql", "20261118_01_gestao_caixa_correcoes.sql", "20261123_01_gestao_caixa_aprovacao.sql",
                             "20261125_01_gestao_caixa_turnos.sql", "20261126_01_gestao_caixa_operacao.sql" })
                    await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", m)));
            }
            var livro = new AcontecimentosService(fonte, Array.Empty<IAcontecimentoAssinante>(), NullLogger<AcontecimentosService>.Instance);
            var caixa = new CaixaService(fonte, livro);
            var gerente = GestaoPapeis.Resolver((_, _) => true, false);
            var hoje = await caixa.Hoje(loja, default);
            await caixa.Abrir(loja, 7, gerente, new AbrirCaixaRequest(hoje, 100), default);
            Guid gaveta;
            await using (var c2 = await fonte.OpenConnectionAsync()) gaveta = await c2.ExecuteScalarAsync<Guid>("SELECT conta_gaveta_id FROM gestao_caixas");
            var maquininha = (await livro.CriarConta(loja, 7, new ContaRequest { ChaveIdempotencia = "maq", Tipo = TipoConta.Recebivel, Subtipo = "maquininha", Nome = "Maquininha" }, default)).Id;

            await livro.Publicar(loja, 7, new PublicarAcontecimentoRequest
            {
                ChaveIdempotencia = "venda:pedido:42", Tipo = "venda", Modulo = ModuloGestao.Pedidos, DataFato = DateTimeOffset.UtcNow, Descricao = "Pedido #42 · retirada",
                Origem = new OrigemRef { Tipo = "pedido", Id = "42" },
                Dados = JsonSerializer.SerializeToElement(new { forma = "din", pagamentos = new object[] { new { forma = "din", valor = 60m }, new { forma = "pix", valor = 20m } } }),
                Lancamentos = new()
                {
                    new LancamentoRequest { Natureza = Natureza.Receita, Valor = 60, ContaId = gaveta, DataPagamento = hoje },
                    new LancamentoRequest { Natureza = Natureza.Receita, Valor = 20, ContaId = maquininha, DataPagamento = hoje },
                },
            }, default);
            await caixa.RegistrarMovimento(loja, 7, gerente, new MovimentoCaixaRequest("sang:1", hoje, MovimentoCaixa.Sangria, 10, "Cofre (pedido #42?)", null), default);

            var fita = (await caixa.CarregarDia(loja, hoje, default))!.Fita;
            var doPedido = fita.Where(l => l.PedidoId == 42).ToList();
            Assert.Equal(2, doPedido.Count);
            Assert.Contains(doPedido, l => l.Forma == "din" && l.Destino == FormaDestino.Gaveta && l.Valor == 60);
            Assert.Contains(doPedido, l => l.Forma == "pix" && l.Destino == FormaDestino.Maquininha && l.Valor == 20);
            // A sangria cita "#42" no texto, mas não é do pedido: o vínculo é do livro, não da descrição.
            Assert.All(fita.Where(l => l.Tipo == "sang"), l => Assert.Null(l.PedidoId));
            // Dinheiro físico = fundo + dinheiro da venda − sangria; o Pix da maquininha não entra na gaveta.
            Assert.Equal(150m, fita.Where(l => l.Forma == "din").Sum(l => l.Valor));
        }
        finally
        {
            await using var admin = new NpgsqlConnection(conexao);
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }
}
