using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Equipe;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// 6C3-C contra PostgreSQL de verdade: entregas do turno apuradas pela cadeia parada → motoboy → usuário → pessoa. Duas lojas,
/// transferência de entregador, cancelamento, entrega pendente, fora do dia, turno confirmado, ajustado e já pago.
/// </summary>
public class GestaoEquipeAtividadeDatabaseTests
{
    private static readonly DateOnly Sexta = new(2026, 10, 23), Sabado = new(2026, 10, 24);

    [DeliveryDatabaseFact]
    public async Task Entregas_do_turno_vem_do_motoboy_ligado_sem_duplicar_e_o_turno_pago_nao_muda()
    {
        var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
        APIBack.Infrastructure.DapperConfiguration.Configure();
        var opcoes = new NpgsqlConnectionStringBuilder(conexao);
        if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
        var schema = "gestao_ativ_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(conexao)) { await admin.OpenAsync(); await admin.ExecuteAsync($"CREATE SCHEMA {schema}"); }
        opcoes.SearchPath = schema;
        var fonte = NpgsqlDataSource.Create(opcoes.ConnectionString);
        try
        {
            Guid loja = Guid.NewGuid(), outraLoja = Guid.NewGuid();
            await using (var c = await fonte.OpenConnectionAsync())
            {
                await c.ExecuteAsync(@"CREATE TABLE estabelecimentos(id uuid PRIMARY KEY, timezone_iana text);
CREATE TABLE usuario(id integer PRIMARY KEY, nome text, deleted_at timestamptz);
CREATE TABLE usuario_estabelecimentos(id_usuario integer, id_estabelecimento uuid, ativo boolean DEFAULT TRUE);
CREATE TABLE motoboy(id integer PRIMARY KEY, id_usuario integer);
CREATE TABLE pedido(id integer PRIMARY KEY, id_estabelecimento uuid, status_pedido integer);
CREATE TABLE delivery_route_stops(id serial PRIMARY KEY, estabelecimento_id uuid, motoboy_id integer, pedido_id integer,
    stop_status text, completed_at_utc timestamptz);
INSERT INTO estabelecimentos VALUES (@loja, 'America/Sao_Paulo'), (@outraLoja, 'America/Sao_Paulo');
INSERT INTO usuario VALUES (7, 'Marina'), (11, 'Rafa'), (12, 'Beto'), (13, 'Caio');
INSERT INTO usuario_estabelecimentos VALUES (7, @loja), (11, @loja), (12, @loja), (13, @loja), (11, @outraLoja);
INSERT INTO motoboy VALUES (1, 11), (2, 12);", new { loja, outraLoja });
                foreach (var m in new[] { "20261105_01_gestao_fundacao.sql", "20261106_01_gestao_documentos.sql", "20261107_01_gestao_vinculos.sql",
                             "20261109_01_gestao_equipe.sql" })
                    await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", m)));

                // Sexta 23/10 em Brasília = 03:00Z de 23/10 até 03:00Z de 24/10.
                const string dentro = "2026-10-23T22:00:00Z", fora = "2026-10-24T05:00:00Z";
                async Task Pedido(int id, Guid l, int status) =>
                    await c.ExecuteAsync("INSERT INTO pedido VALUES (@id, @l, @status)", new { id, l, status });
                async Task Parada(Guid l, int motoboy, int pedido, string situacao, string? quando) =>
                    await c.ExecuteAsync("INSERT INTO delivery_route_stops(estabelecimento_id, motoboy_id, pedido_id, stop_status, completed_at_utc) VALUES (@l, @motoboy, @pedido, @situacao, @quando::timestamptz)",
                        new { l, motoboy, pedido, situacao, quando });
                // 1: entrega comum do Rafa. 2: veio do Beto por transferência e o Rafa concluiu. 3: foi do Rafa e o Beto concluiu.
                await Pedido(1, loja, 3); await Parada(loja, 1, 1, "completed", dentro);
                await Pedido(2, loja, 3); await Parada(loja, 2, 2, "transferred", null); await Parada(loja, 1, 2, "completed", dentro);
                await Pedido(3, loja, 3); await Parada(loja, 1, 3, "transferred", null); await Parada(loja, 2, 3, "completed", dentro);
                // 4: cancelado depois de uma parada concluída. 5: ainda em rota. 6: concluída em outra loja. 7: fora do dia.
                await Pedido(4, loja, 4); await Parada(loja, 1, 4, "completed", dentro);
                await Pedido(5, loja, 2); await Parada(loja, 1, 5, "en_route", null);
                await Pedido(6, outraLoja, 3); await Parada(outraLoja, 1, 6, "completed", dentro);
                await Pedido(7, loja, 3); await Parada(loja, 1, 7, "completed", fora);
                // 8: duas paradas concluídas do mesmo pedido pelo mesmo motoboy contam uma vez.
                await Pedido(8, loja, 3); await Parada(loja, 1, 8, "completed", dentro); await Parada(loja, 1, 8, "completed", "2026-10-23T23:00:00Z");
            }
            var livro = new AcontecimentosService(fonte, Array.Empty<IAcontecimentoAssinante>(), NullLogger<AcontecimentosService>.Instance);
            var equipe = new EquipeService(fonte, livro);
            var banco = (await livro.CriarConta(loja, 7, new ContaRequest { ChaveIdempotencia = "conta-banco", Tipo = TipoConta.Banco, Nome = "Inter" }, default)).Id;

            var rafa = await equipe.SalvarPessoa(loja, 7, null, new PessoaRequest("Rafa", "Entregador", VinculoPessoa.Motoboy,
                new RegraPagamento(Diaria: 60, PorEntrega: 7), "pix", "rafa@pix", 11, "#f80",
                new[] { new DiaPadrao(5, "18:00", "23:00"), new DiaPadrao(6, "18:00", "23:00") }, true, new DateOnly(2026, 10, 1)), default);
            var beto = await equipe.SalvarPessoa(loja, 7, null, new PessoaRequest("Beto", "Entregador", VinculoPessoa.Motoboy,
                new RegraPagamento(Diaria: 60, PorEntrega: 7), "pix", null, 12, null,
                new[] { new DiaPadrao(5, "18:00", "23:00") }, true, new DateOnly(2026, 10, 1)), default);

            // Rafa: 1, 2 e 8. Beto: só o 3 (a transferência não duplica). Cancelado, pendente, outra loja e fora do dia ficam de fora.
            var ativRafa = await equipe.AtividadeDoTurno(loja, rafa.Id, Sexta, default);
            Assert.Equal((3, OrigemAtividade.Apurado), (ativRafa.Entregas, ativRafa.EntregasOrigem));
            Assert.Equal(new[] { 1, 2, 8 }, ativRafa.PedidoIds);
            Assert.Equal(new[] { 3 }, (await equipe.AtividadeDoTurno(loja, beto.Id, Sexta, default)).PedidoIds);
            // O mesmo usuário na outra loja só enxerga as paradas daquela loja (a pessoa é da primeira, então a consulta é por loja).
            // A entrega "fora do dia" de sexta pertence ao sábado (02:00 da loja). Domingo sem nada: zero de verdade, diferente de "sem apuração".
            Assert.Equal(new[] { 7 }, (await equipe.AtividadeDoTurno(loja, rafa.Id, Sabado, default)).PedidoIds);
            var domingoVazio = await equipe.AtividadeDoTurno(loja, rafa.Id, new DateOnly(2026, 10, 25), default);
            Assert.Equal((0, OrigemAtividade.Apurado), (domingoVazio.Entregas, domingoVazio.EntregasOrigem));

            // Sem usuário ligado: não há apuração (nulo), nunca zero. Garçom com comissão: base sem atribuição, nunca presumida.
            var bia = await equipe.SalvarPessoa(loja, 7, null, new PessoaRequest("Bia", "Entregadora", VinculoPessoa.Motoboy,
                new RegraPagamento(Diaria: 50), "pix", null, null, null, new[] { new DiaPadrao(5, "18:00", "23:00") }), default);
            var semVinculo = await equipe.AtividadeDoTurno(loja, bia.Id, Sexta, default);
            Assert.Equal((null, OrigemAtividade.SemVinculo), (semVinculo.Entregas, semVinculo.EntregasOrigem));
            var caio = await equipe.SalvarPessoa(loja, 7, null, new PessoaRequest("Caio", "Garçom", VinculoPessoa.Fixo,
                new RegraPagamento(SalarioMensal: 3000, ComissaoPercentual: 5), "pix", null, 13, null,
                new[] { new DiaPadrao(5, "18:00", "23:00") }, true, new DateOnly(2026, 10, 1)), default);
            var garcom = await equipe.AtividadeDoTurno(loja, caio.Id, Sexta, default);
            Assert.Equal((null, OrigemAtividade.SemEntregador, null, OrigemAtividade.SemAtribuicao),
                (garcom.Entregas, garcom.EntregasOrigem, garcom.BaseComissao, garcom.ComissaoOrigem));
            Assert.NotNull(garcom.Aviso);

            // Confirmação humana: o turno só vai para o livro quando a pessoa confirma. Outro número exige motivo.
            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() =>
                equipe.MudarTurno(loja, 7, new MudarTurnoRequest(rafa.Id, Sexta, StatusTurno.Realizado, Entregas: 5), default))).Codigo);
            Assert.Null((await equipe.Semana(loja, new DateOnly(2026, 10, 19), default)).Grade[rafa.Id][4]!.AcontecimentoId);
            var sexta = await equipe.MudarTurno(loja, 7, new MudarTurnoRequest(rafa.Id, Sexta, StatusTurno.Realizado, Entregas: 3), default);
            Assert.Equal((81m, 3), (sexta.Valor, sexta.Entregas)); // 60 + 3 × 7
            Assert.Contains("entregas apuradas 3, conferidas 3", sexta.Historico.Single().Detalhe);

            // Repetir a confirmação não publica de novo; ajuste com motivo corrige o mesmo item, sem segundo pagamento.
            var repetido = await equipe.MudarTurno(loja, 7, new MudarTurnoRequest(rafa.Id, Sexta, Entregas: 3), default);
            Assert.Equal((sexta.LancamentoId, 1), (repetido.LancamentoId, repetido.Historico.Count));
            var ajustada = await equipe.MudarTurno(loja, 7, new MudarTurnoRequest(rafa.Id, Sexta, Entregas: 4, Motivo: "Entrega a pé sem app"), default);
            Assert.Equal((88m, sexta.LancamentoId), (ajustada.Valor, ajustada.LancamentoId));
            var resumo = Assert.Single(await equipe.Resumo(loja, new DateOnly(2026, 10, 1), default), r => r.PessoaId == rafa.Id);
            Assert.Equal(88m, resumo.Devido);

            // Pago, o turno não muda sozinho: a correção vira desconto ou adiantamento no próximo pagamento.
            await equipe.Pagar(loja, 7, new PagamentoRequest("pag-rafa", rafa.Id, banco, new DateOnly(2026, 10, 28), null), default);
            Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() =>
                equipe.MudarTurno(loja, 7, new MudarTurnoRequest(rafa.Id, Sexta, Entregas: 2, Motivo: "Conferi de novo"), default))).Codigo);
        }
        finally
        {
            await fonte.DisposeAsync();
            await using var admin = new NpgsqlConnection(conexao); await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }
}
