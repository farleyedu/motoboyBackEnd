using System.Text.Json;
using APIBack.Acontecimentos;
using APIBack.GestaoLogica.RepasseIfood;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// Repasse do iFood confirmado a mão (6C1-B) contra PostgreSQL de verdade. A venda é publicada direto no livro (a publicação pela
/// conclusão do pedido é da 6C1-A): a receita nasce a receber no recebível iFood e só vira dinheiro no banco quando o gerente confirma.
/// </summary>
public class GestaoRepasseIfoodDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task Repasse_manual_quita_so_o_recebivel_ifood_uma_vez()
    {
        var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
        APIBack.Infrastructure.DapperConfiguration.Configure();
        var opcoes = new NpgsqlConnectionStringBuilder(conexao);
        if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
        var schema = "gestao_rif_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(conexao)) { await admin.OpenAsync(); await admin.ExecuteAsync($"CREATE SCHEMA {schema}"); }
        opcoes.SearchPath = schema;
        opcoes.MaxPoolSize = 30;
        var fonte = NpgsqlDataSource.Create(opcoes.ConnectionString);
        try
        {
            var loja = Guid.NewGuid();
            var outra = Guid.NewGuid();
            await using (var c = await fonte.OpenConnectionAsync())
            {
                await c.ExecuteAsync(@"CREATE TABLE estabelecimentos(id uuid PRIMARY KEY, timezone_iana text);
CREATE TABLE usuario(id integer PRIMARY KEY, nome text);
INSERT INTO estabelecimentos VALUES (@loja, 'America/Sao_Paulo'), (@outra, 'America/Sao_Paulo'); INSERT INTO usuario VALUES (7, 'Marina');",
                    new { loja, outra });
                foreach (var m in new[] { "20261105_01_gestao_fundacao.sql", "20261118_01_gestao_repasse_ifood.sql", "20261118_01_gestao_repasse_ifood.sql" })
                    await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", m)));
            }
            var livro = new AcontecimentosService(fonte, Array.Empty<IAcontecimentoAssinante>(), NullLogger<AcontecimentosService>.Instance);
            var repasse = new RepasseIfoodService(fonte, livro);
            async Task<Guid> Conta(Guid l, string chave, string tipo, string? subtipo, string nome) =>
                (await livro.CriarConta(l, 7, new ContaRequest { ChaveIdempotencia = chave, Tipo = tipo, Subtipo = subtipo, Nome = nome }, default)).Id;
            var banco = await Conta(loja, "banco", TipoConta.Banco, null, "Banco");
            var ifood = await Conta(loja, "ifood", TipoConta.Recebivel, "ifood", "iFood a receber");
            var gaveta = await Conta(loja, "gaveta", TipoConta.Caixa, null, "Gaveta");
            var bancoDaOutra = await Conta(outra, "banco", TipoConta.Banco, null, "Banco da outra");
            var hoje = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, (await livro.Fuso(loja, default))).DateTime);

            async Task<Guid> Venda(string pedido, params (decimal Valor, Guid Conta, bool Recebido)[] partes) => (await livro.Publicar(loja, 7,
                new PublicarAcontecimentoRequest
                {
                    ChaveIdempotencia = "venda:pedido:" + pedido, Tipo = "venda", Modulo = ModuloGestao.Pedidos, DataFato = DateTimeOffset.UtcNow,
                    Descricao = $"Pedido #{pedido} · iFood", Origem = new OrigemRef { Tipo = "pedido", Id = pedido },
                    Dados = JsonSerializer.SerializeToElement(new { forma = "ifood" }),
                    Lancamentos = partes.Select(p => new LancamentoRequest
                    {
                        Natureza = Natureza.Receita, Valor = p.Valor, ContaId = p.Conta, DataPagamento = p.Recebido ? hoje : null,
                    }).ToList(),
                }, default)).Id;
            LiquidarRepasseIfoodRequest Pedido(string chave, string pedido, decimal valor, Guid? conta = null, string? referencia = null) =>
                new() { ChaveIdempotencia = chave, PedidoId = pedido, ContaDestinoId = conta ?? banco, DataPagamento = hoje,
                    ValorRecebido = valor, Referencia = referencia, ConfirmacaoHumana = true };
            async Task<string> Erro(LiquidarRepasseIfoodRequest r) =>
                (await Assert.ThrowsAsync<GestaoException>(() => repasse.Liquidar(loja, 7, r, default))).Codigo;
            async Task<decimal> Saldo(Guid conta) => (await livro.Contas(loja, default)).Single(k => k.Id == conta).Saldo;

            // Venda de R$ 100 → R$ 100 a receber do iFood; nada no banco.
            var venda = await Venda("501", (100m, ifood, false));
            var aReceber = Assert.Single(await repasse.AReceber(loja, default));
            Assert.Equal(("501", venda, 100m), (aReceber.PedidoId, aReceber.VendaId, aReceber.ValorAReceber));
            Assert.Equal(0m, await Saldo(banco));

            // Validações antes de qualquer gravação.
            Assert.Equal("CAMPO_INVALIDO", await Erro(new LiquidarRepasseIfoodRequest { ChaveIdempotencia = "x", PedidoId = "501", ContaDestinoId = banco,
                DataPagamento = hoje, ValorRecebido = 100m, ConfirmacaoHumana = false }));
            Assert.Equal("CAMPO_INVALIDO", await Erro(new LiquidarRepasseIfoodRequest { ChaveIdempotencia = "x", PedidoId = "501", ContaDestinoId = banco,
                ValorRecebido = 100m, ConfirmacaoHumana = true }));
            Assert.Equal("CAMPO_INVALIDO", await Erro(Pedido("", "501", 100m)));
            Assert.Equal("CONTA_INVALIDA", await Erro(Pedido("x", "501", 100m, bancoDaOutra)));
            Assert.Equal("CONTA_INVALIDA", await Erro(Pedido("x", "501", 100m, ifood)));
            Assert.Equal("SEM_VENDA", await Erro(Pedido("x", "999", 100m)));
            Assert.Equal(0, await Scalar<int>(fonte, "SELECT count(*) FROM gestao_repasses_ifood"));

            // Repasse confirmado: R$ 100 no banco, a receita continua uma só (R$ 100, não R$ 200).
            var ok = await repasse.Liquidar(loja, 7, Pedido("rep-501", "501", 100m, referencia: "Extrato 28/10"), default);
            Assert.Equal((SituacaoRepasseIfood.Liquidado, StatusLancamento.Recebido, 100m, false),
                (ok.Situacao, ok.SituacaoFinanceira, ok.ValorLiquidado, ok.Repetido));
            Assert.Equal(venda, ok.VendaId);
            Assert.NotNull(ok.PagamentoId);
            Assert.Equal(100m, await Saldo(banco));
            Assert.Equal(100m, await Scalar<decimal>(fonte, "SELECT sum(valor) FROM gestao_lancamentos WHERE natureza = 'receita'"));
            Assert.Equal(1, await Scalar<int>(fonte, "SELECT count(*) FROM gestao_lancamentos WHERE natureza = 'receita'"));
            Assert.Empty(await repasse.AReceber(loja, default));
            Assert.Equal("Repasse iFood do pedido #501 (Extrato 28/10)",
                await Scalar<string>(fonte, $"SELECT descricao FROM gestao_acontecimentos WHERE id = '{ok.PagamentoId}'"));

            // Replay: mesma chave e conteúdo devolvem o mesmo; nada a mais no banco. Mesma chave com outro conteúdo é conflito.
            var replay = await repasse.Liquidar(loja, 7, Pedido("rep-501", "501", 100m, referencia: "Extrato 28/10"), default);
            Assert.Equal((ok.Id, ok.PagamentoId, true), (replay.Id, replay.PagamentoId, replay.Repetido));
            Assert.Equal("IDEMPOTENCIA_CONFLITO", await Erro(Pedido("rep-501", "501", 100m, referencia: "Outro")));
            Assert.Equal("JA_LIQUIDADO", await Erro(Pedido("rep-501-b", "501", 100m)));
            Assert.Equal("JA_LIQUIDADO", await Erro(Pedido("rep-501-c", "501", 90m)));
            Assert.Equal(100m, await Saldo(banco));
            Assert.Equal(1, await Scalar<int>(fonte, "SELECT count(*) FROM gestao_liquidacoes"));

            // Repasses concorrentes com chaves diferentes: só um entra.
            await Venda("502", (50m, ifood, false));
            var tentativas = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
            {
                try { await repasse.Liquidar(loja, 7, Pedido($"rep-502-{i}", "502", 50m), default); return "ok"; }
                catch (GestaoException ex) { return ex.Codigo; }
            })));
            Assert.Single(tentativas, t => t == "ok");
            Assert.All(tentativas.Where(t => t != "ok"), t => Assert.Equal("JA_LIQUIDADO", t));
            // Mesma chave em paralelo: um grava, os outros são replay.
            await Venda("507", (20m, ifood, false));
            var mesmas = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
                repasse.Liquidar(loja, 7, Pedido("rep-507", "507", 20m), default))));
            Assert.Single(mesmas.Select(m => m.PagamentoId).Distinct());
            Assert.Equal(170m, await Saldo(banco));
            Assert.Equal(3, await Scalar<int>(fonte, "SELECT count(*) FROM gestao_liquidacoes"));
            Assert.Equal(3, await Scalar<int>(fonte, "SELECT count(*) FROM gestao_repasses_ifood WHERE situacao = 'liquidado'"));

            // Venda estornada: não se recebe.
            var estornada = await Venda("503", (40m, ifood, false));
            await livro.Estornar(loja, 7, estornada, new EstornoRequest { ChaveIdempotencia = "est-503", Motivo = "Pedido cancelado", ConfirmacaoHumana = true }, default);
            Assert.Equal("JA_ESTORNADO", await Erro(Pedido("rep-503", "503", 40m)));

            // Repasse parcial (ou com taxa descontada): nada é quitado nem vira despesa; fica para conciliar.
            await Venda("504", (80m, ifood, false));
            var parcial = await repasse.Liquidar(loja, 7, Pedido("rep-504", "504", 60m, referencia: "Semana 43"), default);
            Assert.Equal((SituacaoRepasseIfood.Divergente, StatusLancamento.AReceber, 0m, (Guid?)null),
                (parcial.Situacao, parcial.SituacaoFinanceira, parcial.ValorLiquidado, parcial.PagamentoId));
            Assert.True((await repasse.Liquidar(loja, 7, Pedido("rep-504", "504", 60m, referencia: "Semana 43"), default)).Repetido);
            var pendente = (await repasse.AReceber(loja, default)).Single(a => a.PedidoId == "504");
            Assert.Equal((80m, 60m), (pendente.ValorAReceber, Assert.Single(pendente.Divergencias).ValorRecebido));
            Assert.Equal(170m, await Saldo(banco));
            Assert.Equal(0, await Scalar<int>(fonte, "SELECT count(*) FROM gestao_lancamentos WHERE natureza = 'despesa'"));
            // Quando o valor inteiro cai, quita.
            Assert.Equal(SituacaoRepasseIfood.Liquidado, (await repasse.Liquidar(loja, 7, Pedido("rep-504-b", "504", 80m), default)).Situacao);
            Assert.Equal(250m, await Saldo(banco));

            // Pagamento na entrega (offline): R$ 30 em dinheiro na gaveta + R$ 70 a receber do iFood. O repasse é só dos R$ 70.
            await Venda("505", (70m, ifood, false), (30m, gaveta, true));
            Assert.Equal(70m, (await repasse.AReceber(loja, default)).Single(a => a.PedidoId == "505").ValorAReceber);
            Assert.Equal(SituacaoRepasseIfood.Divergente, (await repasse.Liquidar(loja, 7, Pedido("rep-505-a", "505", 100m), default)).Situacao);
            var offline = await repasse.Liquidar(loja, 7, Pedido("rep-505", "505", 70m), default);
            Assert.Equal((SituacaoRepasseIfood.Liquidado, 70m), (offline.Situacao, offline.ValorLiquidado));
            Assert.Equal((320m, 30m), (await Saldo(banco), await Saldo(gaveta)));

            // Venda sem parte do iFood (tudo em dinheiro): não há recebível.
            await Venda("506", (25m, gaveta, true));
            Assert.Equal("SEM_RECEBIVEL", await Erro(Pedido("rep-506", "506", 25m)));

            // Receita operacional = soma das vendas não estornadas (o estorno anula os R$ 40), sem duplicar nada.
            Assert.Equal(100m + 50m + 20m + 80m + 70m + 30m + 25m, await Scalar<decimal>(fonte,
                "SELECT sum(valor) FROM gestao_lancamentos WHERE natureza = 'receita'"));
            await using (var c = await fonte.OpenConnectionAsync())
                Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() => c.ExecuteAsync("DELETE FROM gestao_repasses_ifood"))).SqlState);
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
