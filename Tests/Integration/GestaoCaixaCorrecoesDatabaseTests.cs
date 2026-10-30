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
/// 6C2-B contra PostgreSQL de verdade: pedido do operador sem mexer em dinheiro, estorno pelo gerente, aprovação concorrente,
/// correção do fundo reconciliando com o livro, dia fechado, mês fechado e duas lojas.
/// </summary>
public class GestaoCaixaCorrecoesDatabaseTests
{
    private sealed class Ambiente : IAsyncDisposable
    {
        public required NpgsqlDataSource Fonte { get; init; }
        public required AcontecimentosService Livro { get; init; }
        public required CaixaService Caixa { get; init; }
        public required Guid Loja { get; init; }
        public required DateOnly Hoje { get; init; }
        public required string Conexao { get; init; }
        public required string Schema { get; init; }
        public GestaoPapeis.Acesso Gerente { get; } = GestaoPapeis.Resolver((_, _) => true, false);
        public GestaoPapeis.Acesso Operador { get; } = GestaoPapeis.Resolver((m, _) => m is "Caixa", false);

        public static async Task<Ambiente> Criar()
        {
            var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
            APIBack.Infrastructure.DapperConfiguration.Configure();
            var opcoes = new NpgsqlConnectionStringBuilder(conexao);
            if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
            var schema = "gestao_cxc_" + Guid.NewGuid().ToString("N");
            await using (var admin = new NpgsqlConnection(conexao)) { await admin.OpenAsync(); await admin.ExecuteAsync($"CREATE SCHEMA {schema}"); }
            opcoes.SearchPath = schema;
            var fonte = NpgsqlDataSource.Create(opcoes.ConnectionString);
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
                             "20261110_01_gestao_caixa.sql", "20261118_01_gestao_caixa_correcoes.sql" })
                    await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", m)));
            }
            var livro = new AcontecimentosService(fonte, Array.Empty<IAcontecimentoAssinante>(), NullLogger<AcontecimentosService>.Instance);
            var caixa = new CaixaService(fonte, livro);
            return new Ambiente { Fonte = fonte, Livro = livro, Caixa = caixa, Loja = loja, Hoje = await caixa.Hoje(loja, default), Conexao = conexao, Schema = schema };
        }

        public async Task<CaixaDoDia> Abrir(decimal fundo, Guid? loja = null) =>
            await Caixa.Abrir(loja ?? Loja, 8, Operador, new AbrirCaixaRequest(Hoje, fundo), default);

        public async Task<CaixaDoDia> Mover(string chave, string tipo, decimal valor, string? motivo = "teste") =>
            await Caixa.RegistrarMovimento(Loja, 8, Operador, new MovimentoCaixaRequest(chave, Hoje, tipo, valor, motivo, null), default);

        public async Task<decimal> SaldoGaveta(Guid? loja = null) =>
            (await Livro.Contas(loja ?? Loja, default)).First(x => x.Nome == "Gaveta do caixa").Saldo;

        public async Task<decimal> SaldoCofre() => (await Livro.Contas(Loja, default)).First(x => x.Nome == "Cofre").Saldo;

        public async Task<T> Scalar<T>(string sql)
        {
            await using var c = await Fonte.OpenConnectionAsync();
            return (await c.ExecuteScalarAsync<T>(sql))!;
        }

        public async ValueTask DisposeAsync()
        {
            await Fonte.DisposeAsync();
            await using var admin = new NpgsqlConnection(Conexao); await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {Schema} CASCADE");
        }
    }

    private static Guid Id(CaixaDoDia d, string tipo) => Guid.Parse(d.Fita.Last(l => l.Tipo == tipo).Id.Split(':')[0]);

    private static async Task<string> Codigo(Func<Task> acao) => (await Assert.ThrowsAsync<GestaoException>(acao)).Codigo;

    [DeliveryDatabaseFact]
    public async Task Operador_pede_gerente_decide_e_nada_se_apaga()
    {
        await using var a = await Ambiente.Criar();
        await a.Abrir(100);
        await a.Mover("m1", MovimentoCaixa.Suprimento, 50);
        await a.Mover("m2", MovimentoCaixa.Sangria, 30);
        var dia = await a.Mover("m3", MovimentoCaixa.Pagamento, 20, "Gás");
        Assert.Equal(100m, await a.SaldoGaveta()); // 100 + 50 − 30 − 20
        var sangria = Id(dia, "sang");
        Assert.All(dia.Fita.Where(l => l.Tipo is "sup" or "sang" or "pag"), l => Assert.True(l.Corrigivel));
        Assert.False(dia.Fita.First(l => l.Tipo == "abre").Corrigivel);

        // O operador pede: nada se move.
        var pedido = await a.Caixa.Solicitar(a.Loja, 8, a.Operador, sangria, new("p1", "Lancei a sangria errada"), default);
        Assert.Equal(("pendente", "estorno_movimento", 8), (pedido.Status, pedido.Tipo, pedido.SolicitadoPor));
        Assert.Equal(30m, pedido.Movimento!.Valor);
        Assert.Equal(100m, await a.SaldoGaveta());
        Assert.Equal(pedido.Id, (await a.Caixa.Solicitar(a.Loja, 8, a.Operador, sangria, new("p1", "Lancei a sangria errada"), default)).Id);
        Assert.Equal("SOLICITACAO_DUPLICADA", await Codigo(() => a.Caixa.Solicitar(a.Loja, 7, a.Gerente, sangria, new("p2", "outra vez"), default)));
        Assert.Equal("SOLICITACAO_DUPLICADA", await Codigo(() => a.Caixa.EstornarMovimento(a.Loja, 7, a.Gerente, sangria, new("e0", "direto"), default)));
        var fita = (await a.Caixa.CarregarDia(a.Loja, a.Hoje, default))!;
        Assert.Equal(1, fita.SolicitacoesPendentes);
        Assert.Equal(pedido.Id, fita.Fita.Single(l => l.Tipo == "sang").SolicitacaoPendente);

        // O operador não efetiva nada.
        Assert.Equal("SEM_PERMISSAO", await Codigo(() => a.Caixa.Decidir(a.Loja, 8, a.Operador, pedido.Id, new("aprovar", null), default)));
        Assert.Equal("SEM_PERMISSAO", await Codigo(() => a.Caixa.EstornarMovimento(a.Loja, 8, a.Operador, Id(dia, "pag"), new("e1", "tentei"), default)));
        Assert.Single(await a.Caixa.Solicitacoes(a.Loja, 8, a.Operador, null, null, default));
        Assert.Equal("CAMPO_INVALIDO", await Codigo(() => a.Caixa.Decidir(a.Loja, 7, a.Gerente, pedido.Id, new("rejeitar", null), default)));

        // O gerente aprova: estorno do livro, o original continua.
        var aprovado = await a.Caixa.Decidir(a.Loja, 7, a.Gerente, pedido.Id, new("aprovar", null), default);
        Assert.Equal(("aprovada", 7), (aprovado.Status, aprovado.DecididoPor));
        Assert.NotNull(aprovado.CorrecaoId);
        Assert.Equal(130m, await a.SaldoGaveta());
        Assert.Equal(1, await a.Scalar<int>("SELECT count(*) FROM gestao_acontecimentos WHERE correcao = 'estorno'"));
        Assert.Equal(1, await a.Scalar<int>("SELECT count(*) FROM gestao_acontecimentos WHERE tipo = 'sangria'"));
        dia = (await a.Caixa.CarregarDia(a.Loja, a.Hoje, default))!;
        Assert.True(dia.Fita.Single(l => l.Tipo == "sang").Estornada);
        Assert.False(dia.Fita.Single(l => l.Tipo == "sang").Corrigivel);

        // Repetir a decisão não duplica; decisão contrária e novo estorno são recusados.
        Assert.Equal(aprovado.CorrecaoId, (await a.Caixa.Decidir(a.Loja, 7, a.Gerente, pedido.Id, new("aprovar", null), default)).CorrecaoId);
        Assert.Equal("JA_DECIDIDA", await Codigo(() => a.Caixa.Decidir(a.Loja, 7, a.Gerente, pedido.Id, new("rejeitar", "mudei de ideia"), default)));
        Assert.Equal("JA_ESTORNADO", await Codigo(() => a.Caixa.EstornarMovimento(a.Loja, 7, a.Gerente, sangria, new("e2", "de novo"), default)));
        Assert.Equal("JA_ESTORNADO", await Codigo(() => a.Caixa.Solicitar(a.Loja, 8, a.Operador, sangria, new("p3", "de novo"), default)));
        Assert.Equal(130m, await a.SaldoGaveta());

        // Rejeição guarda o motivo e libera o movimento para outro pedido.
        var pag = Id(dia, "pag");
        var p4 = await a.Caixa.Solicitar(a.Loja, 8, a.Operador, pag, new("p4", "Foi no PIX"), default);
        var rej = await a.Caixa.Decidir(a.Loja, 7, a.Gerente, p4.Id, new("rejeitar", "O gás foi pago em dinheiro mesmo"), default);
        Assert.Equal(("rejeitada", "O gás foi pago em dinheiro mesmo", (Guid?)null), (rej.Status, rej.MotivoDecisao, rej.CorrecaoId));
        Assert.Equal(130m, await a.SaldoGaveta());
        Assert.Equal(1, (await a.Caixa.Solicitacoes(a.Loja, 7, a.Gerente, "rejeitada", null, default)).Count);

        // O gerente estorna direto o pagamento, com motivo e responsável.
        dia = await a.Caixa.EstornarMovimento(a.Loja, 7, a.Gerente, pag, new("e3", "Pagamento lançado em duplicidade"), default);
        Assert.Equal(150m, await a.SaldoGaveta());
        Assert.Equal(7, await a.Scalar<int>("SELECT responsavel_id FROM gestao_acontecimentos WHERE correcao = 'estorno' ORDER BY registrado_em DESC LIMIT 1"));

        // A solicitação é imutável depois de decidida; ninguém apaga.
        await using var c = await a.Fonte.OpenConnectionAsync();
        Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() => c.ExecuteAsync("DELETE FROM gestao_caixa_solicitacoes"))).SqlState);
        Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() =>
            c.ExecuteAsync("UPDATE gestao_caixa_solicitacoes SET status = 'pendente', decidido_em = NULL, decidido_por = NULL, correcao_id = NULL, motivo_decisao = NULL"))).SqlState);
    }

    [DeliveryDatabaseFact]
    public async Task Duas_aprovacoes_ao_mesmo_tempo_geram_um_so_efeito()
    {
        await using var a = await Ambiente.Criar();
        await a.Abrir(100);
        var dia = await a.Mover("m1", MovimentoCaixa.Sangria, 40);
        var pedido = await a.Caixa.Solicitar(a.Loja, 8, a.Operador, Id(dia, "sang"), new("p1", "Errei o valor"), default);
        Assert.Equal(60m, await a.SaldoGaveta());

        var resultados = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Task.Run(async () =>
            {
                try { return (Guid?)(await a.Caixa.Decidir(a.Loja, 7, a.Gerente, pedido.Id, new("aprovar", null), default)).CorrecaoId; }
                catch (GestaoException ex) { Assert.Contains(ex.Codigo, new[] { "JA_DECIDIDA", "JA_ESTORNADO" }); return null; }
            })));
        Assert.Contains(resultados, r => r is not null);
        Assert.Equal(1, await a.Scalar<int>("SELECT count(*) FROM gestao_acontecimentos WHERE correcao = 'estorno'"));
        Assert.Equal(100m, await a.SaldoGaveta());

        // Aprovar e rejeitar juntos: um só vence.
        var dia2 = await a.Mover("m2", MovimentoCaixa.Suprimento, 10);
        var p2 = await a.Caixa.Solicitar(a.Loja, 8, a.Operador, Id(dia2, "sup"), new("p2", "Não houve reforço"), default);
        var mistas = await Task.WhenAll(new[] { "aprovar", "rejeitar" }.Select(d => Task.Run(async () =>
        {
            try { await a.Caixa.Decidir(a.Loja, 7, a.Gerente, p2.Id, new(d, "decisão do gerente"), default); return "ok"; }
            catch (GestaoException ex) { return ex.Codigo; }
        })));
        Assert.Equal(1, mistas.Count(r => r == "ok"));
        Assert.Equal(1, mistas.Count(r => r == "JA_DECIDIDA"));
        var final = (await a.Caixa.Solicitacoes(a.Loja, 7, a.Gerente, null, null, default)).Single(s => s.Id == p2.Id);
        var estornos = await a.Scalar<int>("SELECT count(*) FROM gestao_acontecimentos WHERE correcao = 'estorno'");
        Assert.Equal(final.Status == "aprovada" ? 2 : 1, estornos);
    }

    [DeliveryDatabaseFact]
    public async Task So_movimento_de_gaveta_e_corrigivel_e_so_com_o_dia_aberto()
    {
        await using var a = await Ambiente.Criar();
        var abertura = await a.Abrir(100);
        var gaveta = await a.Scalar<Guid>("SELECT conta_gaveta_id FROM gestao_caixas");
        await a.Livro.Publicar(a.Loja, 7, new PublicarAcontecimentoRequest
        {
            ChaveIdempotencia = "venda:1", Tipo = "venda", Modulo = ModuloGestao.Pedidos, DataFato = DateTimeOffset.UtcNow, Descricao = "Pedido #1",
            Origem = new OrigemRef { Tipo = "pedido", Id = "1" }, Dados = JsonSerializer.SerializeToElement(new { forma = "din" }),
            Lancamentos = new() { new LancamentoRequest { Natureza = Natureza.Receita, Valor = 30, ContaId = gaveta, DataPagamento = a.Hoje } },
        }, default);
        var dia = await a.Mover("m1", MovimentoCaixa.Sangria, 10);
        dia = await a.Caixa.RegistrarMovimento(a.Loja, 8, a.Operador, new MovimentoCaixaRequest("m2", a.Hoje, MovimentoCaixa.Devolucao, 30, "Pedido cancelado", "1"), default);
        var venda = Guid.Parse(dia.Fita.First(l => l.Tipo == "venda").Id.Split(':')[0]);
        Assert.False(dia.Fita.First(l => l.Tipo == "venda").Corrigivel);

        // Venda, abertura e a devolução que já estornou a venda inteira não passam pelo Caixa: nada de cancelar pedido por acidente.
        Assert.Equal("NAO_ELEGIVEL", await Codigo(() => a.Caixa.Solicitar(a.Loja, 8, a.Operador, venda, new("p1", "cancelar a venda"), default)));
        Assert.Equal("NAO_ELEGIVEL", await Codigo(() => a.Caixa.EstornarMovimento(a.Loja, 7, a.Gerente, venda, new("e1", "cancelar a venda"), default)));
        Assert.Equal("NAO_ELEGIVEL", await Codigo(() => a.Caixa.Solicitar(a.Loja, 8, a.Operador, Id(dia, "abre"), new("p2", "abertura"), default)));
        Assert.Equal("NAO_ELEGIVEL", await Codigo(() => a.Caixa.Solicitar(a.Loja, 8, a.Operador, Id(dia, "canc"), new("p3", "estorno"), default)));
        Assert.Equal("NAO_ENCONTRADO", await Codigo(() => a.Caixa.Solicitar(a.Loja, 8, a.Operador, Guid.NewGuid(), new("p4", "não existe"), default)));
        Assert.Equal("CAMPO_INVALIDO", await Codigo(() => a.Caixa.Solicitar(a.Loja, 8, a.Operador, Id(dia, "sang"), new("p5", "x"), default)));
        Assert.Equal(1, await a.Scalar<int>("SELECT count(*) FROM gestao_acontecimentos WHERE correcao = 'estorno'")); // só o da devolução

        // Pedido aberto e o dia fecha: aprovar não passa; o gerente rejeita.
        var pedido = await a.Caixa.Solicitar(a.Loja, 8, a.Operador, Id(dia, "sang"), new("p6", "Sangria errada"), default);
        var esperado = await a.SaldoGaveta();
        await a.Caixa.Fechar(a.Loja, 8, a.Operador, new FecharCaixaRequest(a.Hoje, new ContagemCaixa(new Dictionary<int, int> { [50] = 1 }, esperado - 50),
            new Dictionary<string, decimal>(), null, 20), default);
        Assert.Equal("FECHADO", await Codigo(() => a.Caixa.Decidir(a.Loja, 7, a.Gerente, pedido.Id, new("aprovar", null), default)));
        Assert.Equal("FECHADO", await Codigo(() => a.Caixa.EstornarMovimento(a.Loja, 7, a.Gerente, Id(dia, "sang"), new("e2", "tarde"), default)));
        Assert.Equal("FECHADO", await Codigo(() => a.Caixa.Solicitar(a.Loja, 8, a.Operador, Id(dia, "sang"), new("p7", "tarde"), default)));
        Assert.Equal("pendente", (await a.Caixa.Solicitacoes(a.Loja, 7, a.Gerente, null, null, default)).Single().Status);
        Assert.Equal("rejeitada", (await a.Caixa.Decidir(a.Loja, 7, a.Gerente, pedido.Id, new("rejeitar", "Dia já fechado"), default)).Status);
    }

    [DeliveryDatabaseFact]
    public async Task Fundo_corrigido_reconcilia_com_o_livro_e_o_original_fica()
    {
        await using var a = await Ambiente.Criar();
        await a.Abrir(100);
        await a.Mover("m1", MovimentoCaixa.Sangria, 70);
        Assert.Equal((30m, -100m), (await a.SaldoGaveta(), await a.SaldoCofre()));

        // Operador pede 100 → 120; o dinheiro não se move.
        var resposta = await a.Caixa.CorrigirFundo(a.Loja, 8, a.Operador, a.Hoje, new("f1", 100, 120, "Contei errado na abertura"), default);
        Assert.False(resposta.Efetivada);
        Assert.Equal(("pendente", "corrigir_fundo", 100m, 120m), (resposta.Solicitacao!.Status, resposta.Solicitacao.Tipo, resposta.Solicitacao.FundoAnterior!.Value, resposta.Solicitacao.FundoNovo!.Value));
        Assert.Equal(30m, await a.SaldoGaveta());
        Assert.Equal(resposta.Solicitacao.Id, (await a.Caixa.CorrigirFundo(a.Loja, 8, a.Operador, a.Hoje, new("f1", 100, 120, "Contei errado na abertura"), default)).Solicitacao!.Id);
        Assert.Equal("SOLICITACAO_DUPLICADA", await Codigo(() => a.Caixa.CorrigirFundo(a.Loja, 8, a.Operador, a.Hoje, new("f2", 100, 130, "outro valor"), default)));
        Assert.Equal("SOLICITACAO_DUPLICADA", await Codigo(() => a.Caixa.CorrigirFundo(a.Loja, 7, a.Gerente, a.Hoje, new("f3", 100, 130, "direto"), default)));
        Assert.Equal("CONFLITO", await Codigo(() => a.Caixa.CorrigirFundo(a.Loja, 8, a.Operador, a.Hoje, new("f4", 90, 130, "tela velha"), default)));

        var aprovado = await a.Caixa.Decidir(a.Loja, 7, a.Gerente, resposta.Solicitacao.Id, new("aprovar", null), default);
        Assert.Equal("aprovada", aprovado.Status);
        var dia = (await a.Caixa.CarregarDia(a.Loja, a.Hoje, default))!;
        Assert.Equal((120m, 100m), (dia.Fundo, dia.FundoOriginal));
        Assert.Equal((100m, 120m), (dia.CorrecoesFundo!.Single().FundoAnterior, dia.CorrecoesFundo.Single().FundoNovo));
        Assert.Equal(120m, dia.Fita.First(l => l.Tipo == "abre").Valor);
        Assert.Equal(new[] { "abre", "sang" }, dia.Fita.Select(l => l.Tipo)); // não vira sangria/suprimento
        // Gaveta = fundo efetivo − movimentos; o cofre cobriu a diferença. O fundo original no banco não mudou.
        Assert.Equal((50m, -120m), (await a.SaldoGaveta(), await a.SaldoCofre()));
        Assert.Equal(dia.Fundo - 70m, await a.SaldoGaveta());
        Assert.Equal(100m, await a.Scalar<decimal>("SELECT fundo FROM gestao_caixa_dias"));
        Assert.Equal(1, await a.Scalar<int>($"SELECT count(*) FROM gestao_acontecimentos WHERE tipo = '{CaixaService.TipoCorrecaoFundo}'"));

        // Gerente corrige direto 120 → 90 (a gaveta devolve R$ 30 ao cofre); repetir a chave não duplica.
        var f5 = await a.Caixa.CorrigirFundo(a.Loja, 7, a.Gerente, a.Hoje, new("f5", 120, 90, "Era 90"), default);
        Assert.True(f5.Efetivada);
        await a.Caixa.CorrigirFundo(a.Loja, 7, a.Gerente, a.Hoje, new("f5", 120, 90, "Era 90"), default);
        Assert.Equal((20m, -90m, 90m), (await a.SaldoGaveta(), await a.SaldoCofre(), f5.Dia!.Fundo));
        Assert.Equal(2, f5.Dia.CorrecoesFundo!.Count);
        Assert.Equal("CONFLITO", await Codigo(() => a.Caixa.CorrigirFundo(a.Loja, 7, a.Gerente, a.Hoje, new("f6", 120, 100, "tela velha"), default)));
        Assert.Equal("CAMPO_INVALIDO", await Codigo(() => a.Caixa.CorrigirFundo(a.Loja, 7, a.Gerente, a.Hoje, new("f7", 90, 90, "igual"), default)));
        Assert.Equal("CAMPO_INVALIDO", await Codigo(() => a.Caixa.CorrigirFundo(a.Loja, 7, a.Gerente, a.Hoje, new("f8", 90, -1, "negativo"), default)));
        // A gaveta tem R$ 20: não dá para o fundo cair R$ 30.
        Assert.Equal("SALDO_INSUFICIENTE", await Codigo(() => a.Caixa.CorrigirFundo(a.Loja, 7, a.Gerente, a.Hoje, new("f9", 90, 60, "baixar"), default)));
        Assert.Equal(20m, await a.SaldoGaveta());

        // Depois do fechamento, nenhuma correção de fundo.
        await a.Caixa.Fechar(a.Loja, 8, a.Operador, new FecharCaixaRequest(a.Hoje, new ContagemCaixa(new Dictionary<int, int> { [20] = 1 }, 0),
            new Dictionary<string, decimal>(), null, 20), default);
        Assert.Equal("FECHADO", await Codigo(() => a.Caixa.CorrigirFundo(a.Loja, 7, a.Gerente, a.Hoje, new("f10", 90, 95, "tarde"), default)));
        Assert.Equal("FECHADO", await Codigo(() => a.Caixa.CorrigirFundo(a.Loja, 8, a.Operador, a.Hoje, new("f11", 90, 95, "tarde"), default)));
        Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var c = await a.Fonte.OpenConnectionAsync();
            await c.ExecuteAsync("DELETE FROM gestao_caixa_fundo_ajustes");
        })).SqlState);
    }

    [DeliveryDatabaseFact]
    public async Task Mes_fechado_nao_deixa_efeito_pela_metade()
    {
        await using var a = await Ambiente.Criar();
        await a.Abrir(100);
        var dia = await a.Mover("m1", MovimentoCaixa.Sangria, 10);
        var pedidoFundo = await a.Caixa.CorrigirFundo(a.Loja, 8, a.Operador, a.Hoje, new("f1", 100, 110, "Contei errado"), default);
        var abertura = await a.Scalar<Guid>("SELECT abertura_id FROM gestao_caixa_dias");
        await a.Scalar<int>($"INSERT INTO gestao_meses_fechados (estabelecimento_id, mes, acontecimento_id, fechado_por) VALUES ('{a.Loja}', '{a.Hoje.Year}-{a.Hoje.Month:00}-01', '{abertura}', 7) RETURNING 1");

        // Correção do fundo é transferência nova (não é estorno): mês fechado recusa e o pedido continua pendente.
        Assert.Equal("MES_FECHADO", await Codigo(() => a.Caixa.Decidir(a.Loja, 7, a.Gerente, pedidoFundo.Solicitacao!.Id, new("aprovar", null), default)));
        Assert.Equal("pendente", (await a.Caixa.Solicitacoes(a.Loja, 7, a.Gerente, null, null, default)).Single().Status);
        Assert.Equal(0, await a.Scalar<int>("SELECT count(*) FROM gestao_caixa_fundo_ajustes"));
        Assert.Equal(0, await a.Scalar<int>($"SELECT count(*) FROM gestao_acontecimentos WHERE tipo = '{CaixaService.TipoCorrecaoFundo}'"));
        Assert.Equal(90m, await a.SaldoGaveta());

        // Estorno é correção: o mês fechado aceita.
        var estornado = await a.Caixa.EstornarMovimento(a.Loja, 7, a.Gerente, Id(dia, "sang"), new("e1", "Sangria errada"), default);
        Assert.Equal(100m, await a.SaldoGaveta());
        Assert.True(estornado.Fita.Single(l => l.Tipo == "sang").Estornada);
    }

    [DeliveryDatabaseFact]
    public async Task Uma_loja_nao_enxerga_nem_corrige_a_outra()
    {
        await using var a = await Ambiente.Criar();
        var loja2 = Guid.NewGuid();
        await using (var c = await a.Fonte.OpenConnectionAsync()) await c.ExecuteAsync("INSERT INTO estabelecimentos VALUES (@loja2, 'America/Sao_Paulo')", new { loja2 });
        await a.Abrir(100);
        await a.Abrir(50, loja2);
        var dia = await a.Mover("m1", MovimentoCaixa.Sangria, 10);
        var sangria = Id(dia, "sang");
        var pedido = await a.Caixa.Solicitar(a.Loja, 8, a.Operador, sangria, new("p1", "Sangria errada"), default);

        Assert.Equal("NAO_ENCONTRADO", await Codigo(() => a.Caixa.Solicitar(loja2, 8, a.Operador, sangria, new("p2", "da outra loja"), default)));
        Assert.Equal("NAO_ENCONTRADO", await Codigo(() => a.Caixa.EstornarMovimento(loja2, 7, a.Gerente, sangria, new("e1", "da outra loja"), default)));
        Assert.Equal("NAO_ENCONTRADO", await Codigo(() => a.Caixa.Decidir(loja2, 7, a.Gerente, pedido.Id, new("aprovar", null), default)));
        Assert.Empty(await a.Caixa.Solicitacoes(loja2, 7, a.Gerente, null, null, default));
        Assert.Single(await a.Caixa.Solicitacoes(a.Loja, 7, a.Gerente, null, null, default));

        // O fundo corrigido de uma loja não toca na outra; a mesma chave vale em lojas diferentes.
        await a.Caixa.CorrigirFundo(a.Loja, 7, a.Gerente, a.Hoje, new("f1", 100, 120, "Era 120"), default);
        var r2 = await a.Caixa.CorrigirFundo(loja2, 7, a.Gerente, a.Hoje, new("f1", 50, 60, "Era 60"), default);
        Assert.Equal((60m, 50m), (r2.Dia!.Fundo, r2.Dia.FundoOriginal));
        Assert.Equal(60m, await a.SaldoGaveta(loja2));
        Assert.Equal(110m, await a.SaldoGaveta()); // 120 − 10
        Assert.Equal(1, (await a.Caixa.CarregarDia(a.Loja, a.Hoje, default))!.CorrecoesFundo!.Count);
    }
}
