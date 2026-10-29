using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Equipe;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// Equipe contra PostgreSQL de verdade: escala recorrente, funcionário que faltou no sábado, correção com motivo (ajuste e
/// estorno), adiantamento compensado, desconto e pagamento parcial com saldo.
/// </summary>
public class GestaoEquipeDatabaseTests
{
    private static readonly DateOnly Sexta = new(2026, 10, 23), Sabado = new(2026, 10, 24), Segunda = new(2026, 10, 19);

    [DeliveryDatabaseFact]
    public async Task Faltou_no_sabado_correcao_adiantamento_desconto_e_pagamento_parcial()
    {
        var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
        APIBack.Infrastructure.DapperConfiguration.Configure();
        var opcoes = new NpgsqlConnectionStringBuilder(conexao);
        if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
        var schema = "gestao_eq_" + Guid.NewGuid().ToString("N");
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
                             "20261109_01_gestao_equipe.sql", "20261109_01_gestao_equipe.sql" })
                    await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", m)));
            }
            var livro = new AcontecimentosService(fonte, Array.Empty<IAcontecimentoAssinante>(), NullLogger<AcontecimentosService>.Instance);
            var equipe = new EquipeService(fonte, livro);
            var banco = (await livro.CriarConta(loja, 7, new ContaRequest { ChaveIdempotencia = "conta-banco", Tipo = TipoConta.Banco, Nome = "Inter" }, default)).Id;

            // Motoboy com diária de R$ 60 + R$ 7 por entrega, sexta e sábado das 18h às 23h.
            var rafa = await equipe.SalvarPessoa(loja, 7, null, new PessoaRequest("Rafa", "Entregador", VinculoPessoa.Motoboy,
                new RegraPagamento(SalarioMensal: 5000, Diaria: 60, PorEntrega: 7), "pix", "rafa@pix", null, "#f80",
                new[] { new DiaPadrao(5, "18:00", "23:00"), new DiaPadrao(6, "18:00", "23:00") }, true, new DateOnly(2026, 10, 1)), default);
            Assert.Null(rafa.Regra.SalarioMensal); // campo de outro vínculo é descartado
            var editada = await equipe.SalvarPessoa(loja, 7, rafa.Id, new PessoaRequest("Rafa Lima", "Entregador", VinculoPessoa.Motoboy,
                rafa.Regra, "pix", "rafa@pix", null, "#f80", rafa.EscalaPadrao, Versao: rafa.Versao), default);
            Assert.Equal((2, new DateOnly(2026, 10, 1)), (editada.Versao, editada.Desde));
            Assert.Equal("IDEMPOTENCIA_CONFLITO", (await Assert.ThrowsAsync<GestaoException>(() => equipe.SalvarPessoa(loja, 7, rafa.Id,
                new PessoaRequest("Rafa", "x", VinculoPessoa.Motoboy, rafa.Regra, "pix", null, null, null, null, Versao: 1), default))).Codigo);

            // Escala recorrente: a semana mostra sexta e sábado previstos, sem nada no livro.
            var semana = await equipe.Semana(loja, Segunda, default);
            var linha = semana.Grade[rafa.Id];
            Assert.Equal(new[] { 4, 5 }, linha.Select((t, i) => (t, i)).Where(x => x.t != null).Select(x => x.i));
            Assert.Null(linha[4]!.Id);
            Assert.Equal(120m, semana.Totais.Previsto);

            // Sexta realizada: 12 entregas e chuva → R$ 154 a pagar no livro.
            var sexta = await equipe.MudarTurno(loja, 7, new MudarTurnoRequest(rafa.Id, Sexta, StatusTurno.Realizado, Entregas: 12,
                Adicionais: new[] { new AdicionalTurno("Chuva", 10) }), default);
            Assert.Equal(154m, sexta.Valor);
            Assert.NotNull(sexta.LancamentoId);
            // Faltou no sábado sem justificativa: zera a diária, nada vai para o livro, o previsto continua visível.
            var sabado = await equipe.MudarTurno(loja, 7, new MudarTurnoRequest(rafa.Id, Sabado, StatusTurno.Falta), default);
            Assert.Equal((0m, 60m, (Guid?)null), (sabado.Valor, sabado.Previsto, sabado.AcontecimentoId));
            semana = await equipe.Semana(loja, Segunda, default);
            Assert.Equal(new TotaisSemana(214m, 154m, 2, 1, 1), semana.Totais);

            // Correção da sexta: sem motivo não; com motivo vira ajuste no livro e linha no histórico.
            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() =>
                equipe.MudarTurno(loja, 7, new MudarTurnoRequest(rafa.Id, Sexta, Entregas: 10), default))).Codigo);
            sexta = await equipe.MudarTurno(loja, 7, new MudarTurnoRequest(rafa.Id, Sexta, Entregas: 10, Motivo: "Duas entregas eram de outro"), default);
            Assert.Equal(140m, sexta.Valor);
            Assert.Equal(new[] { "realizado", "ajuste" }, sexta.Historico.Select(h => h.Acao));
            Assert.Equal((154m, 140m), (sexta.Historico[1].ValorAnterior, sexta.Historico[1].ValorNovo));

            // Atestado chegou: falta justificada paga a diária; depois descobriu-se que não valia → estorno, revisão 1.
            sabado = await equipe.MudarTurno(loja, 7, new MudarTurnoRequest(rafa.Id, Sabado, Justificada: true), default);
            Assert.Equal(60m, sabado.Valor);
            Assert.NotNull(sabado.AcontecimentoId);
            sabado = await equipe.MudarTurno(loja, 7, new MudarTurnoRequest(rafa.Id, Sabado, Justificada: false, Motivo: "Atestado inválido"), default);
            Assert.Equal((0m, 1, (Guid?)null), (sabado.Valor, sabado.Revisao, sabado.AcontecimentoId));

            // Turno futuro não se confirma.
            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() =>
                equipe.MudarTurno(loja, 7, new MudarTurnoRequest(rafa.Id, new DateOnly(2099, 1, 2), StatusTurno.Realizado), default))).Codigo);

            // Adiantamento de R$ 50 (sai do banco agora) e desconto de R$ 10 do uniforme na sexta.
            await equipe.Adiantar(loja, 7, new AdiantamentoRequest("adiant-1", rafa.Id, 50, banco, new DateOnly(2026, 10, 25), "vale"), default);
            await equipe.Descontar(loja, 7, new DescontoRequest("desc-1", rafa.Id, sexta.LancamentoId!.Value, 10, "Uniforme"), default);
            var resumo = Assert.Single(await equipe.Resumo(loja, new DateOnly(2026, 10, 1), default));
            Assert.Equal((140m, 130m, 0m, 130m, 50m, 1), (resumo.Apurado, resumo.Devido, resumo.Pago, resumo.APagar, resumo.AdiantamentosAbertos, resumo.Faltas));

            // Pagamento parcial de R$ 50: o adiantamento abate R$ 50 (fica 80), paga 50 e os 30 viram saldo a pagar.
            var pag = await equipe.Pagar(loja, 7, new PagamentoRequest("pag-1", rafa.Id, banco, new DateOnly(2026, 10, 28), 50), default);
            Assert.Equal((50m, 50m, 30m), (pag.Pago, pag.Compensado, pag.Restante));
            Assert.NotNull(pag.SaldoLancamentoId);
            Assert.Equal(0m, pag.Resumo.AdiantamentosAbertos);
            // A mesma chave de novo não paga duas vezes.
            var repetido = await equipe.Pagar(loja, 7, new PagamentoRequest("pag-1", rafa.Id, banco, new DateOnly(2026, 10, 28), 50), default);
            Assert.Equal((50m, 30m), (repetido.Pago, repetido.Restante));
            Assert.Empty(repetido.Liquidados);
            // Quita o resto.
            var resto = await equipe.Pagar(loja, 7, new PagamentoRequest("pag-2", rafa.Id, banco, new DateOnly(2026, 10, 28), null), default);
            Assert.Equal((30m, 0m), (resto.Pago, resto.Restante));
            Assert.Equal((80m, 0m), (resto.Resumo.Pago, resto.Resumo.APagar));

            // Turno já pago não muda sozinho.
            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() =>
                equipe.MudarTurno(loja, 7, new MudarTurnoRequest(rafa.Id, Sexta, Entregas: 11, Motivo: "Esqueci uma"), default))).Codigo);

            // A semana traz o histórico dos turnos gravados, com o nome de quem mexeu (o detalhe do turno no Painel lê daqui).
            semana = await equipe.Semana(loja, Segunda, default);
            var sextaNaSemana = semana.Grade[rafa.Id][4]!;
            Assert.Equal(new[] { "realizado", "ajuste" }, sextaNaSemana.Historico.Select(h => h.Acao));
            Assert.All(sextaNaSemana.Historico, h => Assert.Equal("Marina", h.PorNome));
            Assert.Equal("Marina", semana.Pessoas.Single().CriadoPorNome);

            // Colaborador eventual no sábado: jornada avulsa → realizado → devido → pago → saída no banco.
            var saldoAntes = (await livro.Contas(loja, default)).Single(x => x.Id == banco).Saldo;
            var bia = await equipe.SalvarPessoa(loja, 7, null, new PessoaRequest("Bia", "Eventual", VinculoPessoa.Freelancer,
                new RegraPagamento(PorTurno: 90), "dinheiro", null, null, null, Array.Empty<DiaPadrao>()), default);
            var avulso = await equipe.MudarTurno(loja, 7, new MudarTurnoRequest(bia.Id, Sabado, Entrada: "18:00", Saida: "23:00", Avulso: true,
                PagamentoAvulso: new PagamentoAvulso("turno", 100)), default);
            Assert.Equal(("previsto", true, (Guid?)null), (avulso.Status, avulso.Avulso, avulso.AcontecimentoId));
            avulso = await equipe.MudarTurno(loja, 7, new MudarTurnoRequest(bia.Id, Sabado, StatusTurno.Realizado), default);
            Assert.Equal(100m, avulso.Valor);
            var devido = (await equipe.Resumo(loja, new DateOnly(2026, 10, 1), default)).Single(r => r.PessoaId == bia.Id);
            Assert.Equal((100m, 100m, 0m), (devido.Devido, devido.APagar, devido.Pago));
            var pagoBia = await equipe.Pagar(loja, 7, new PagamentoRequest("pag-bia", bia.Id, banco, new DateOnly(2026, 10, 25), null), default);
            Assert.Equal((100m, 0m), (pagoBia.Pago, pagoBia.Restante));
            Assert.Equal(saldoAntes - 100m, (await livro.Contas(loja, default)).Single(x => x.Id == banco).Saldo);

            await using (var c = await fonte.OpenConnectionAsync())
            {
                Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() => c.ExecuteAsync("DELETE FROM gestao_turnos"))).SqlState);
                Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() => c.ExecuteAsync("UPDATE gestao_turnos_historico SET motivo = 'x'"))).SqlState);
                Assert.Equal(3, await c.ExecuteScalarAsync<int>("SELECT count(*) FROM gestao_pessoas_historico")); // Rafa (2 versões) e Bia
            }
        }
        finally
        {
            await fonte.DisposeAsync();
            await using var admin = new NpgsqlConnection(conexao); await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }

    private sealed class Relogio(DateTimeOffset agora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => agora;
    }

    /// <summary>
    /// L5: turno de setembro (fechado) confirmado em outubro vira ajuste no mês de hoje, ligado ao dia original; nome e vínculo
    /// mudam e cada versão fica no histórico (a regra mostra desde quando vale e qual era a anterior).
    /// </summary>
    [DeliveryDatabaseFact]
    public async Task Turno_de_mes_fechado_vira_ajuste_e_cadastro_guarda_versoes()
    {
        var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
        APIBack.Infrastructure.DapperConfiguration.Configure();
        var opcoes = new NpgsqlConnectionStringBuilder(conexao);
        if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
        var schema = "gestao_eq_" + Guid.NewGuid().ToString("N");
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
                             "20261109_01_gestao_equipe.sql" })
                    await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", m)));
            }
            var livro = new AcontecimentosService(fonte, Array.Empty<IAcontecimentoAssinante>(), NullLogger<AcontecimentosService>.Instance);
            var equipe = new EquipeService(fonte, livro, new Relogio(new DateTimeOffset(2026, 10, 29, 15, 0, 0, TimeSpan.Zero)));

            var bia = await equipe.SalvarPessoa(loja, 7, null, new PessoaRequest("Bia Sousa", "Cozinha", VinculoPessoa.Diarista,
                new RegraPagamento(Diaria: 90), "pix", null, null, null, new[] { new DiaPadrao(2, "10:00", "16:00") }, true, new DateOnly(2026, 9, 1)), default);
            var setembro = new DateOnly(2026, 9, 15); // terça
            await livro.FecharMes(loja, 7, new DateOnly(2026, 9, 1), new FecharMesRequest { ChaveIdempotencia = "fecha-set" }, default);

            // Sem motivo não; com motivo entra em outubro, apontando para 15/09.
            var erro = await Assert.ThrowsAsync<GestaoException>(() =>
                equipe.MudarTurno(loja, 7, new MudarTurnoRequest(bia.Id, setembro, StatusTurno.Realizado), default));
            Assert.Contains("09/2026 está fechado", erro.Message);
            var turno = await equipe.MudarTurno(loja, 7, new MudarTurnoRequest(bia.Id, setembro, StatusTurno.Realizado, Motivo: "Esqueci de confirmar"), default);
            Assert.Equal(90m, turno.Valor);
            Assert.Equal("09/2026 fechado: lançado em 10/2026", turno.Historico.Single().Detalhe);
            var publicado = (await livro.Obter(loja, turno.AcontecimentoId!.Value, default))!;
            Assert.Equal(new DateOnly(2026, 10, 1), publicado.Lancamentos.Single().MesReferencia);
            Assert.Contains("ajuste (09/2026 fechado)", publicado.Descricao);
            Assert.Equal(("2026-09-15", "2026-10-29", "2026-09-01"), (publicado.Dados.GetProperty("data").GetString(),
                publicado.Dados.GetProperty("lancadoEm").GetString(), publicado.Dados.GetProperty("mesFechado").GetString()));
            // Aparece no resumo de outubro (onde entrou), não no de setembro.
            Assert.Equal(90m, (await equipe.Resumo(loja, new DateOnly(2026, 10, 1), default)).Single().APagar);
            Assert.Equal(0m, (await equipe.Resumo(loja, new DateOnly(2026, 9, 1), default)).Single().APagar);
            // Corrigir depois é ajuste comum (com motivo) sobre o lançamento de outubro.
            turno = await equipe.MudarTurno(loja, 7, new MudarTurnoRequest(bia.Id, setembro, Saida: "18:00", Motivo: "Ficou até as 18h"), default);
            Assert.Equal(90m, turno.Valor); // diária não muda com a hora
            Assert.Equal(2, turno.Historico.Count);

            // Nome e vínculo mudam; a regra nova vale para o que ainda não foi lançado.
            var nova = await equipe.SalvarPessoa(loja, 7, bia.Id, new PessoaRequest("Beatriz Sousa", "Cozinha", VinculoPessoa.Hora,
                new RegraPagamento(PorHora: 20), "pix", null, null, null, bia.EscalaPadrao, Versao: bia.Versao), default);
            Assert.Equal(("Beatriz Sousa", VinculoPessoa.Hora), (nova.Nome, nova.Vinculo));
            var outubro = await equipe.MudarTurno(loja, 7, new MudarTurnoRequest(bia.Id, new DateOnly(2026, 10, 27), StatusTurno.Realizado), default);
            Assert.Equal(120m, outubro.Valor); // 6 h × R$ 20

            var versoes = await equipe.HistoricoDaPessoa(loja, bia.Id, default);
            Assert.Equal(new[] { 2, 1 }, versoes.Select(v => v.Versao));
            Assert.Equal((VinculoPessoa.Hora, 20m, "Marina"), (versoes[0].Vinculo, versoes[0].Regra.PorHora!.Value, versoes[0].PorNome));
            Assert.Equal(("Bia Sousa", 90m), (versoes[1].Nome, versoes[1].Regra.Diaria!.Value));
            Assert.Equal("NAO_ENCONTRADO", (await Assert.ThrowsAsync<GestaoException>(() => equipe.HistoricoDaPessoa(Guid.NewGuid(), bia.Id, default))).Codigo);
        }
        finally
        {
            await fonte.DisposeAsync();
            await using var admin = new NpgsqlConnection(conexao); await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }

    /// <summary>Integração 6A: o colaborador que vai usar o Zippy é ligado a um usuário com acesso à loja, e a um só.</summary>
    [DeliveryDatabaseFact]
    public async Task Colaborador_ligado_ao_usuario_da_loja()
    {
        var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
        APIBack.Infrastructure.DapperConfiguration.Configure();
        var opcoes = new NpgsqlConnectionStringBuilder(conexao);
        if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
        var schema = "gestao_eq_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(conexao)) { await admin.OpenAsync(); await admin.ExecuteAsync($"CREATE SCHEMA {schema}"); }
        opcoes.SearchPath = schema;
        var fonte = NpgsqlDataSource.Create(opcoes.ConnectionString);
        try
        {
            var loja = Guid.NewGuid();
            var outraLoja = Guid.NewGuid();
            await using (var c = await fonte.OpenConnectionAsync())
            {
                await c.ExecuteAsync(@"CREATE TABLE estabelecimentos(id uuid PRIMARY KEY, timezone_iana text);
CREATE TABLE usuario(id integer PRIMARY KEY, nome text, email text, deleted_at timestamptz);
CREATE TABLE usuario_estabelecimentos(id serial PRIMARY KEY, id_usuario integer, id_estabelecimento uuid, ativo boolean);
INSERT INTO estabelecimentos VALUES (@loja, 'America/Sao_Paulo'), (@outraLoja, 'America/Sao_Paulo');
INSERT INTO usuario VALUES (7, 'Marina', 'marina@x.com', null), (12, 'Júlia', 'julia@x.com', null), (13, 'Pedro', 'pedro@y.com', null);
INSERT INTO usuario_estabelecimentos (id_usuario, id_estabelecimento, ativo) VALUES (7, @loja, true), (12, @loja, true), (13, @outraLoja, true);",
                    new { loja, outraLoja });
                foreach (var m in new[] { "20261105_01_gestao_fundacao.sql", "20261106_01_gestao_documentos.sql", "20261107_01_gestao_vinculos.sql",
                             "20261109_01_gestao_equipe.sql" })
                    await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", m)));
            }
            var livro = new AcontecimentosService(fonte, Array.Empty<IAcontecimentoAssinante>(), NullLogger<AcontecimentosService>.Instance);
            var equipe = new EquipeService(fonte, livro);
            PessoaRequest Julia(int? usuarioId, int? versao = null) => new("Júlia", "Caixa", VinculoPessoa.Fixo, new RegraPagamento(SalarioMensal: 1800), "pix",
                null, usuarioId, null, Array.Empty<DiaPadrao>(), Versao: versao);

            // Usuário de outra loja não serve.
            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() => equipe.SalvarPessoa(loja, 7, null, Julia(13), default))).Codigo);
            var julia = await equipe.SalvarPessoa(loja, 7, null, Julia(null), default);
            Assert.Null((await equipe.UsuariosDaLoja(loja, default)).Single(u => u.UsuarioId == 12).PessoaId);
            // Convite aceito: liga a Júlia ao usuário 12. Outra pessoa não pode usar o mesmo usuário; a própria pode salvar de novo.
            julia = await equipe.SalvarPessoa(loja, 7, julia.Id, Julia(12, julia.Versao), default);
            Assert.Equal(12, julia.UsuarioId);
            Assert.Equal(julia.Id, (await equipe.UsuariosDaLoja(loja, default)).Single(u => u.UsuarioId == 12).PessoaId);
            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() =>
                equipe.SalvarPessoa(loja, 7, null, new PessoaRequest("Outra", "x", VinculoPessoa.Fixo, null, "pix", null, 12, null, null), default))).Codigo);
            julia = await equipe.SalvarPessoa(loja, 7, julia.Id, Julia(12, julia.Versao), default);
            Assert.Equal(new[] { "Júlia", "Marina" }, (await equipe.UsuariosDaLoja(loja, default)).Select(u => u.Nome));
        }
        finally
        {
            await fonte.DisposeAsync();
            await using var admin = new NpgsqlConnection(conexao); await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }
}
