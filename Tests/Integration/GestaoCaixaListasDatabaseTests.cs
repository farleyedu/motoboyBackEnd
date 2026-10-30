using APIBack.Acontecimentos;
using APIBack.GestaoLogica.CaixaListas;
using Dapper;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>6C2-C contra PostgreSQL de verdade: padrões por loja, duas lojas independentes, desativar sem sumir, idempotência e nomes repetidos.</summary>
public class GestaoCaixaListasDatabaseTests
{
    private sealed class Ambiente : IAsyncDisposable
    {
        public required NpgsqlDataSource Fonte { get; init; }
        public required CaixaListasService Listas { get; init; }
        public required Guid LojaA { get; init; }
        public required Guid LojaB { get; init; }
        public required string Conexao { get; init; }
        public required string Schema { get; init; }

        public static async Task<Ambiente> Criar()
        {
            var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
            APIBack.Infrastructure.DapperConfiguration.Configure();
            var opcoes = new NpgsqlConnectionStringBuilder(conexao);
            if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
            var schema = "gestao_cxl_" + Guid.NewGuid().ToString("N");
            await using (var admin = new NpgsqlConnection(conexao)) { await admin.OpenAsync(); await admin.ExecuteAsync($"CREATE SCHEMA {schema}"); }
            opcoes.SearchPath = schema;
            var fonte = NpgsqlDataSource.Create(opcoes.ConnectionString);
            var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
            await using (var c = await fonte.OpenConnectionAsync())
            {
                await c.ExecuteAsync(@"CREATE TABLE estabelecimentos(id uuid PRIMARY KEY, timezone_iana text);
CREATE TABLE usuario(id integer PRIMARY KEY, nome text);
INSERT INTO estabelecimentos VALUES (@a, 'America/Sao_Paulo'), (@b, 'America/Sao_Paulo'); INSERT INTO usuario VALUES (7, 'Marina');", new { a, b });
                var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261119_01_gestao_caixa_listas.sql"));
                await c.ExecuteAsync(sql);
                await c.ExecuteAsync(sql); // idempotente
            }
            return new Ambiente { Fonte = fonte, Listas = new CaixaListasService(fonte), LojaA = a, LojaB = b, Conexao = conexao, Schema = schema };
        }

        public async ValueTask DisposeAsync()
        {
            await Fonte.DisposeAsync();
            await using var admin = new NpgsqlConnection(Conexao);
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {Schema} CASCADE");
        }
    }

    [DeliveryDatabaseFact]
    public async Task PrimeiraConsultaCriaOsPadroesAtivosUmaSoVez()
    {
        await using var e = await Ambiente.Criar();
        var primeira = await e.Listas.Listar(e.LojaA, true, default);
        var segunda = await e.Listas.Listar(e.LojaA, true, default);
        Assert.Equal(GrupoLista.Padroes.Count, primeira.Count);
        Assert.Equal(primeira.Select(o => o.Id), segunda.Select(o => o.Id));
        Assert.All(primeira, o => { Assert.True(o.Ativo); Assert.Equal("padrao", o.Origem); });
        Assert.Contains(primeira, o => o.Grupo == "sang" && o.Nome == "Cofre");
        Assert.Contains(primeira, o => o.Grupo == "fechamento" && o.Nome == "Não sei explicar");
    }

    [DeliveryDatabaseFact]
    public async Task DuasLojasMantemListasIndependentes()
    {
        await using var e = await Ambiente.Criar();
        var cofreA = (await e.Listas.Listar(e.LojaA, true, default)).First(o => o.Grupo == "sang" && o.Nome == "Cofre");
        await e.Listas.Atualizar(e.LojaA, cofreA.Id, new AtualizarOpcaoRequest("Cofre da loja", false, null), default);
        await e.Listas.Criar(e.LojaA, 7, new CriarOpcaoRequest("a1", "sang", "Malote"), default);

        var listaB = await e.Listas.Listar(e.LojaB, true, default);
        Assert.Equal(GrupoLista.Padroes.Count, listaB.Count);
        Assert.Contains(listaB, o => o.Nome == "Cofre" && o.Ativo);
        Assert.DoesNotContain(listaB, o => o.Nome == "Malote");
        // Loja B não consegue mexer na opção da loja A.
        var erro = await Assert.ThrowsAsync<GestaoException>(() => e.Listas.Atualizar(e.LojaB, cofreA.Id, new AtualizarOpcaoRequest(null, true, null), default));
        Assert.Equal(404, erro.Status);
    }

    [DeliveryDatabaseFact]
    public async Task DesativarERenomearPadraoMantemAOpcaoENaoVoltaNaProximaConsulta()
    {
        await using var e = await Ambiente.Criar();
        var frete = (await e.Listas.Listar(e.LojaA, true, default)).First(o => o.Nome == "Frete");
        var renomeada = await e.Listas.Atualizar(e.LojaA, frete.Id, new AtualizarOpcaoRequest("Frete e entrega", false, null), default);
        Assert.Equal("padrao", renomeada.Origem);
        Assert.False(renomeada.Ativo);

        var gerente = await e.Listas.Listar(e.LojaA, true, default);
        var operador = await e.Listas.Listar(e.LojaA, false, default);
        Assert.Equal(GrupoLista.Padroes.Count, gerente.Count); // não duplicou o padrão renomeado
        Assert.Contains(gerente, o => o.Id == frete.Id && !o.Ativo && o.Nome == "Frete e entrega");
        Assert.DoesNotContain(operador, o => o.Id == frete.Id);
        Assert.Equal(GrupoLista.Padroes.Count - 1, operador.Count);
    }

    [DeliveryDatabaseFact]
    public async Task IncluirIdempotenteReativaNomeExistenteEBarraRepetido()
    {
        await using var e = await Ambiente.Criar();
        var nova = await e.Listas.Criar(e.LojaA, 7, new CriarOpcaoRequest("k1", "pag", "Vale-transporte"), default);
        var repetida = await e.Listas.Criar(e.LojaA, 7, new CriarOpcaoRequest("k1", "pag", "Vale-transporte"), default);
        Assert.Equal(nova.Id, repetida.Id);
        Assert.Equal("personalizada", nova.Origem);

        await e.Listas.Atualizar(e.LojaA, nova.Id, new AtualizarOpcaoRequest(null, false, null), default);
        var reativada = await e.Listas.Criar(e.LojaA, 7, new CriarOpcaoRequest("k2", "pag", "  vale-transporte "), default);
        Assert.Equal(nova.Id, reativada.Id);
        Assert.True(reativada.Ativo);

        var frete = (await e.Listas.Listar(e.LojaA, true, default)).First(o => o.Grupo == "pag" && o.Nome == "Frete");
        var erro = await Assert.ThrowsAsync<GestaoException>(() => e.Listas.Atualizar(e.LojaA, nova.Id, new AtualizarOpcaoRequest("FRETE", null, null), default));
        Assert.Equal("NOME_REPETIDO", erro.Codigo);
        Assert.NotEqual(frete.Id, nova.Id);

        // O mesmo nome em outro grupo é permitido.
        var outroGrupo = await e.Listas.Criar(e.LojaA, 7, new CriarOpcaoRequest(null, "sup", "Frete"), default);
        Assert.Equal("sup", outroGrupo.Grupo);
    }

    [DeliveryDatabaseFact]
    public async Task ValidaGrupoNomeEAlteracaoVazia()
    {
        await using var e = await Ambiente.Criar();
        var a = await Assert.ThrowsAsync<GestaoException>(() => e.Listas.Criar(e.LojaA, 7, new CriarOpcaoRequest("x", "din", "VR"), default));
        var b = await Assert.ThrowsAsync<GestaoException>(() => e.Listas.Criar(e.LojaA, 7, new CriarOpcaoRequest("y", "sang", " a "), default));
        var c = await Assert.ThrowsAsync<GestaoException>(() => e.Listas.Atualizar(e.LojaA, Guid.NewGuid(), new AtualizarOpcaoRequest(null, null, null), default));
        Assert.All(new[] { a, b, c }, x => Assert.Equal(422, x.Status));
        var d = await Assert.ThrowsAsync<GestaoException>(() => e.Listas.Atualizar(e.LojaA, Guid.NewGuid(), new AtualizarOpcaoRequest("Ok", null, null), default));
        Assert.Equal(404, d.Status);
    }
}
