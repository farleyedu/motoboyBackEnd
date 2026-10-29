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
/// Um dia de caixa contra PostgreSQL de verdade: abertura com fundo, vendas, suprimento, sangria, pagamento em dinheiro,
/// devolução (estorno da venda e devolução parcial), contagem às cegas, fechamento com quebra e recolhimento, correção posterior.
/// </summary>
public class GestaoCaixaDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task Dia_de_caixa_do_fundo_ao_fechamento_com_correcao()
    {
        var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
        APIBack.Infrastructure.DapperConfiguration.Configure();
        var opcoes = new NpgsqlConnectionStringBuilder(conexao);
        if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
        var schema = "gestao_cx_" + Guid.NewGuid().ToString("N");
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
INSERT INTO estabelecimentos VALUES (@loja, 'America/Sao_Paulo'); INSERT INTO usuario VALUES (7, 'Marina'), (8, 'Júlia do caixa');", new { loja });
                foreach (var m in new[] { "20261105_01_gestao_fundacao.sql", "20261106_01_gestao_documentos.sql", "20261107_01_gestao_vinculos.sql",
                             "20261110_01_gestao_caixa.sql", "20261110_01_gestao_caixa.sql" })
                    await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", m)));
            }
            var livro = new AcontecimentosService(fonte, Array.Empty<IAcontecimentoAssinante>(), NullLogger<AcontecimentosService>.Instance);
            var caixa = new CaixaService(fonte, livro);
            var gerente = GestaoPapeis.Resolver((_, _) => true, false);
            var operador = GestaoPapeis.Resolver((m, _) => m is "Caixa", false);
            var hoje = await caixa.Hoje(loja, default);

            Assert.False((await caixa.CarregarDia(loja, hoje, default))!.Aberto);
            Assert.Null(await caixa.CarregarDia(loja, hoje.AddDays(1), default));
            Assert.Equal("SEM_CAIXA", (await Assert.ThrowsAsync<GestaoException>(() => caixa.RegistrarMovimento(loja, 8, operador,
                new MovimentoCaixaRequest("m0", hoje, MovimentoCaixa.Suprimento, 10, null, null), default))).Codigo);
            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() =>
                caixa.Abrir(loja, 8, operador, new AbrirCaixaRequest(hoje.AddDays(-1), 100), default))).Codigo);

            // A operadora abre com R$ 100 de fundo (o cofre completa a gaveta vazia).
            var dia = await caixa.Abrir(loja, 8, operador, new AbrirCaixaRequest(hoje, 100), default);
            Assert.True(dia.Aberto);
            Assert.Equal(("abre", 100m), (dia.Fita[0].Tipo, dia.Fita[0].Valor));
            Assert.Equal(dia.AbertoEm, (await caixa.Abrir(loja, 8, operador, new AbrirCaixaRequest(hoje, 100), default)).AbertoEm);

            // Vendas do dia (a Integração publica): dinheiro na gaveta, Pix e crédito na maquininha.
            var maquininha = (await livro.CriarConta(loja, 7, new ContaRequest { ChaveIdempotencia = "maq", Tipo = TipoConta.Recebivel, Subtipo = "maquininha", Nome = "Stone" }, default)).Id;
            var gaveta = await Scalar<Guid>(fonte, "SELECT conta_gaveta_id FROM gestao_caixas");
            async Task Venda(string pedido, string forma, decimal valor, Guid conta, bool recebido) => await livro.Publicar(loja, 7, new PublicarAcontecimentoRequest
            {
                ChaveIdempotencia = "venda:" + pedido, Tipo = "venda", Modulo = ModuloGestao.Pedidos, DataFato = DateTimeOffset.UtcNow,
                Descricao = $"Pedido #{pedido} · balcão", Origem = new OrigemRef { Tipo = "pedido", Id = pedido },
                Dados = JsonSerializer.SerializeToElement(new { forma }),
                Lancamentos = new() { new LancamentoRequest { Natureza = Natureza.Receita, Valor = valor, ContaId = conta, DataPagamento = recebido ? hoje : null } },
            }, default);
            await Venda("141", FormaCaixa.Dinheiro, 42.90m, gaveta, true);
            await Venda("142", FormaCaixa.Pix, 38m, maquininha, true);
            await Venda("143", FormaCaixa.Credito, 96.50m, maquininha, false);

            await caixa.RegistrarMovimento(loja, 8, operador, new MovimentoCaixaRequest("m1", hoje, MovimentoCaixa.Suprimento, 50, "Reforço de troco", null), default);
            Assert.Equal("SEM_SALDO", (await Assert.ThrowsAsync<GestaoException>(() => caixa.RegistrarMovimento(loja, 8, operador,
                new MovimentoCaixaRequest("m2", hoje, MovimentoCaixa.Sangria, 200, "Cofre", null), default))).Codigo);
            await caixa.RegistrarMovimento(loja, 8, operador, new MovimentoCaixaRequest("m3", hoje, MovimentoCaixa.Sangria, 100, "Cofre", null), default);
            await caixa.RegistrarMovimento(loja, 8, operador, new MovimentoCaixaRequest("m4", hoje, MovimentoCaixa.Pagamento, 20, "Entregador de gás", null), default);
            // Pedido 141 devolvido inteiro: estorna a venda. Troco errado de R$ 5: despesa de devolução.
            await caixa.RegistrarMovimento(loja, 8, operador, new MovimentoCaixaRequest("m5", hoje, MovimentoCaixa.Devolucao, 42.90m, "Pedido cancelado", "141"), default);
            dia = await caixa.RegistrarMovimento(loja, 8, operador, new MovimentoCaixaRequest("m6", hoje, MovimentoCaixa.Devolucao, 5, "Troco errado", null), default);
            Assert.Equal(new[] { "abre", "venda", "venda", "venda", "sup", "sang", "pag", "canc", "canc" }, dia.Fita.Select(l => l.Tipo));
            Assert.Equal(25m, dia.Fita.Where(l => l.Forma == FormaCaixa.Dinheiro).Sum(l => l.Valor)); // 100 + 42,90 + 50 − 100 − 20 − 42,90 − 5
            Assert.Equal(1, await Scalar<int>(fonte, "SELECT count(*) FROM gestao_acontecimentos WHERE correcao = 'estorno'"));

            // Contagem às cegas: R$ 23 contados, esperado R$ 25. Sem justificativa não fecha.
            var contagem = new ContagemCaixa(new Dictionary<int, int> { [20] = 1 }, 3);
            Assert.Equal(new ConferenciaDaContagem(25m, 23m, -2m), await caixa.ConferirContagem(loja, 8, hoje, contagem, default));
            var maq = new Dictionary<string, decimal> { ["pix"] = 38, ["cre"] = 90, ["deb"] = 0 };
            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() =>
                caixa.Fechar(loja, 8, operador, new FecharCaixaRequest(hoje, contagem, maq, null, 10), default))).Codigo);
            dia = await caixa.Fechar(loja, 8, operador, new FecharCaixaRequest(hoje, contagem, maq, "Troco dado errado", 10), default);
            var f = dia.Fechamento!;
            Assert.Equal((25m, 23m, 10m, 13m), (f.Esperado, f.Contado, f.Fica, f.Cofre));
            Assert.Equal((38m, 96.50m), (f.MaquininhaEsperado["pix"], f.MaquininhaEsperado["cre"]));
            Assert.False(dia.Aberto);
            Assert.Equal(2m, await Scalar<decimal>(fonte, "SELECT sum(valor) FROM gestao_lancamentos WHERE categoria = 'quebra_de_caixa'"));
            var saldos = (await livro.Contas(loja, default)).ToDictionary(x => x.Nome, x => x.Saldo);
            // Gaveta = o troco que ficou. Cofre (que começou vazio): −100 da abertura − 50 do suprimento + 100 da sangria + 13 recolhidos.
            Assert.Equal((10m, -37m), (saldos["Gaveta do caixa"], saldos["Cofre"]));
            Assert.Equal("FECHADO", (await Assert.ThrowsAsync<GestaoException>(() => caixa.RegistrarMovimento(loja, 8, operador,
                new MovimentoCaixaRequest("m7", hoje, MovimentoCaixa.Suprimento, 10, null, null), default))).Codigo);
            Assert.Equal(2, await Scalar<int>(fonte, "SELECT count(*) FROM gestao_caixa_contagens"));

            // Correção do gerente: os R$ 2 estavam na outra gaveta. Novo registro, o fechamento original fica.
            var corrigido = await caixa.AjustarFechamento(loja, 7, hoje, new AjustarFechamentoRequest(25, "Achei R$ 2 na outra gaveta"), default);
            Assert.Equal((23m, 25m), (corrigido.Contado, corrigido.Ajustes.Single().ContadoNovo));
            Assert.Equal(2m, await Scalar<decimal>(fonte, "SELECT sum(valor) FROM gestao_lancamentos WHERE categoria = 'sobra_de_caixa'"));
            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() =>
                caixa.AjustarFechamento(loja, 7, hoje, new AjustarFechamentoRequest(25, "de novo"), default))).Codigo);
            Assert.Single(await caixa.Fechamentos(loja, new DateOnly(hoje.Year, hoje.Month, 1), default));
            await using (var c = await fonte.OpenConnectionAsync())
                Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() => c.ExecuteAsync("DELETE FROM gestao_caixa_contagens"))).SqlState);
        }
        finally
        {
            await fonte.DisposeAsync();
            await using var admin = new NpgsqlConnection(conexao); await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }

    /// <summary>
    /// Integração 6A: diária e compra pagas em dinheiro pela gaveta quitam o que já estava a pagar (Equipe e Compras), sem criar outra
    /// despesa; pagamento avulso vira despesa; o fechamento recolhe ao cofre; a fita mostra tudo com o nome de quem abriu.
    /// </summary>
    [DeliveryDatabaseFact]
    public async Task Diaria_e_compra_pagas_pela_gaveta()
    {
        var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
        APIBack.Infrastructure.DapperConfiguration.Configure();
        var opcoes = new NpgsqlConnectionStringBuilder(conexao);
        if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
        var schema = "gestao_cx_" + Guid.NewGuid().ToString("N");
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
INSERT INTO estabelecimentos VALUES (@loja, 'America/Sao_Paulo'); INSERT INTO usuario VALUES (7, 'Marina'), (8, 'Júlia do caixa');", new { loja });
                foreach (var m in new[] { "20261105_01_gestao_fundacao.sql", "20261106_01_gestao_documentos.sql", "20261107_01_gestao_vinculos.sql",
                             "20261109_01_gestao_equipe.sql", "20261110_01_gestao_caixa.sql" })
                    await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", m)));
            }
            var livro = new AcontecimentosService(fonte, Array.Empty<IAcontecimentoAssinante>(), NullLogger<AcontecimentosService>.Instance);
            var equipe = new APIBack.GestaoLogica.Equipe.EquipeService(fonte, livro);
            var caixa = new CaixaService(fonte, livro, null, equipe);
            var operador = GestaoPapeis.Resolver((m, _) => m is "Caixa", false);
            var hoje = await caixa.Hoje(loja, default);

            // Ontem a Bia (eventual) fez um turno de R$ 100, confirmado pela gerente: está a pagar na Equipe.
            var bia = await equipe.SalvarPessoa(loja, 7, null, new APIBack.GestaoLogica.Equipe.PessoaRequest("Bia", "Eventual", "freelancer",
                new APIBack.GestaoLogica.Equipe.RegraPagamento(PorTurno: 100), "dinheiro", null, null, null, Array.Empty<APIBack.GestaoLogica.Equipe.DiaPadrao>()), default);
            await equipe.MudarTurno(loja, 7, new APIBack.GestaoLogica.Equipe.MudarTurnoRequest(bia.Id, hoje.AddDays(-1), Entrada: "10:00", Saida: "15:00", Avulso: true), default);
            await equipe.MudarTurno(loja, 7, new APIBack.GestaoLogica.Equipe.MudarTurnoRequest(bia.Id, hoje.AddDays(-1), "realizado"), default);
            // Uma compra de hortifruti a pagar (Compras publica assim).
            var compra = await livro.Publicar(loja, 7, new PublicarAcontecimentoRequest
            {
                ChaveIdempotencia = "compra-hf", Tipo = "compra", Modulo = ModuloGestao.Compras, DataFato = DateTimeOffset.UtcNow.AddHours(-1),
                Descricao = "Compra · Hortifruti do Zé", Origem = new OrigemRef { Tipo = "compra", Id = "hf-1" },
                Lancamentos = new() { new LancamentoRequest { Natureza = Natureza.Despesa, Valor = 30, Status = StatusLancamento.APagar, Categoria = "insumos" } },
            }, default);
            var lancCompra = compra.Acontecimento.Lancamentos[0].Id;

            var dia = await caixa.Abrir(loja, 8, operador, new AbrirCaixaRequest(hoje, 200), default);
            Assert.Equal("Júlia do caixa", dia.PorNome);
            var aPagar = await caixa.APagar(loja, default);
            Assert.Equal(("Bia", 100m), (aPagar.Equipe.Single().Nome, aPagar.Equipe.Single().APagar));
            Assert.Equal((lancCompra, 30m), (aPagar.Compras.Single().LancamentoId, aPagar.Compras.Single().Valor));

            // Diária paga pela operadora na gaveta: quita o turno na Equipe (não cria outra despesa).
            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() => caixa.RegistrarMovimento(loja, 8, operador,
                new MovimentoCaixaRequest("pg-bia", hoje, MovimentoCaixa.Pagamento, 150, "Diária · Bia", null, PessoaId: bia.Id), default))).Codigo);
            await caixa.RegistrarMovimento(loja, 8, operador, new MovimentoCaixaRequest("pg-bia", hoje, MovimentoCaixa.Pagamento, 100, "Diária · Bia", null, PessoaId: bia.Id), default);
            await caixa.RegistrarMovimento(loja, 8, operador, new MovimentoCaixaRequest("pg-bia", hoje, MovimentoCaixa.Pagamento, 100, "Diária · Bia", null, PessoaId: bia.Id), default);
            var resumoBia = (await equipe.Resumo(loja, new DateOnly(hoje.AddDays(-1).Year, hoje.AddDays(-1).Month, 1), default)).Single();
            Assert.Equal((100m, 0m), (resumoBia.Pago, resumoBia.APagar));
            Assert.Empty((await caixa.APagar(loja, default)).Equipe);

            // Compra paga inteira pela gaveta; valor diferente é recusado; repetir a chave não paga de novo.
            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() => caixa.RegistrarMovimento(loja, 8, operador,
                new MovimentoCaixaRequest("pg-hf", hoje, MovimentoCaixa.Pagamento, 20, "Hortifruti", null, LancamentoId: lancCompra), default))).Codigo);
            await caixa.RegistrarMovimento(loja, 8, operador, new MovimentoCaixaRequest("pg-hf", hoje, MovimentoCaixa.Pagamento, 30, "Hortifruti", null, LancamentoId: lancCompra), default);
            await caixa.RegistrarMovimento(loja, 8, operador, new MovimentoCaixaRequest("pg-hf", hoje, MovimentoCaixa.Pagamento, 30, "Hortifruti", null, LancamentoId: lancCompra), default);
            Assert.Empty((await caixa.APagar(loja, default)).Compras);

            // Pagamento avulso em dinheiro vira despesa da gaveta.
            dia = await caixa.RegistrarMovimento(loja, 8, operador, new MovimentoCaixaRequest("pg-gas", hoje, MovimentoCaixa.Pagamento, 15, "Entregador de gás", null), default);
            Assert.Equal(15m, await Scalar<decimal>(fonte, "SELECT sum(valor) FROM gestao_lancamentos WHERE categoria = 'despesas_caixa' AND natureza = 'despesa'"));
            Assert.Equal(new[] { "abre", "pag", "pag", "pag" }, dia.Fita.Select(l => l.Tipo));
            Assert.Equal(55m, dia.Fita.Sum(l => l.Valor)); // 200 − 100 − 30 − 15
            // Só duas despesas a pagar existiram (turno e compra): o pagamento pela gaveta não criou outra.
            Assert.Equal(1, await Scalar<int>(fonte, "SELECT count(*) FROM gestao_lancamentos WHERE categoria = 'equipe'"));

            // Fecha certinho com R$ 55, deixa R$ 20 de troco: R$ 35 recolhidos ao cofre.
            dia = await caixa.Fechar(loja, 8, operador, new FecharCaixaRequest(hoje, new ContagemCaixa(new Dictionary<int, int> { [50] = 1, [5] = 1 }, 0),
                new Dictionary<string, decimal>(), null, 20), default);
            Assert.Equal((55m, 35m, "Júlia do caixa"), (dia.Fechamento!.Esperado, dia.Fechamento.Cofre, dia.Fechamento.OperadorNome));
            var saldos = (await livro.Contas(loja, default)).ToDictionary(x => x.Nome, x => x.Saldo);
            Assert.Equal((20m, -165m), (saldos["Gaveta do caixa"], saldos["Cofre"])); // cofre: −200 da abertura + 35 recolhidos
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
