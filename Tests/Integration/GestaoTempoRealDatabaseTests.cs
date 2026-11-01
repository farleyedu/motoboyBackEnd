using APIBack.Acontecimentos;
using APIBack.Security;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// Tempo real da Gestão (6C4) contra PostgreSQL de verdade: o aviso de uma transação de outro módulo só sai no commit, uma vez,
/// para a loja certa; rollback e replay não avisam.
/// </summary>
public class GestaoTempoRealDatabaseTests
{
    private sealed record Recebido(Guid Loja, string[] Recursos, Guid Id);

    private sealed class Painel : IGestaoTempoRealPublisher
    {
        public List<Recebido> Avisos { get; } = new();
        public Task Invalidar(Guid loja, IReadOnlyList<string> recursos, Guid id, long versao, CancellationToken ct)
        {
            lock (Avisos) Avisos.Add(new Recebido(loja, recursos.ToArray(), id));
            return Task.CompletedTask;
        }
        public Recebido[] De(Guid loja) { lock (Avisos) return Avisos.Where(a => a.Loja == loja).ToArray(); }
    }

    private sealed class Banco : IAsyncDisposable
    {
        public NpgsqlDataSource Source { get; private set; } = null!;
        public Guid Loja { get; } = Guid.NewGuid();
        public Guid Outra { get; } = Guid.NewGuid();
        public Painel Painel { get; } = new();
        public AcontecimentosService Livro { get; private set; } = null!;
        public GestaoTempoRealOuvinte Ouvinte { get; private set; } = null!;
        private ServiceProvider _sp = null!;
        private readonly string _schema = "gestao_rt_" + Guid.NewGuid().ToString("N");
        private string _base = "";

        public static async Task<Banco> Criar()
        {
            APIBack.Infrastructure.DapperConfiguration.Configure();
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE"));
            if (options.Host != "127.0.0.1" && options.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
            options.PersistSecurityInfo = true;
            var b = new Banco { _base = options.ConnectionString };
            await using (var admin = new NpgsqlConnection(b._base)) { await admin.OpenAsync(); await admin.ExecuteAsync($"CREATE SCHEMA {b._schema}"); }
            options.SearchPath = b._schema; options.MaxPoolSize = 10;
            b.Source = NpgsqlDataSource.Create(options.ConnectionString);
            await using (var c = await b.Source.OpenConnectionAsync())
            {
                await c.ExecuteAsync(@"CREATE TABLE estabelecimentos(id uuid PRIMARY KEY, timezone_iana text);
CREATE TABLE usuario(id integer PRIMARY KEY, nome text);
INSERT INTO estabelecimentos VALUES (@Loja, 'America/Sao_Paulo'), (@Outra, 'America/Sao_Paulo');
INSERT INTO usuario VALUES (7, 'Farley');", new { b.Loja, b.Outra });
                await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261105_01_gestao_fundacao.sql")));
            }
            var services = new ServiceCollection();
            services.AddSingleton(b.Source);
            services.AddSingleton<IGestaoTempoRealPublisher>(b.Painel);
            services.AddSingleton<IAcontecimentoAssinante, GestaoTempoRealAssinante>();
            services.AddLogging();
            services.AddScoped<IAcontecimentosService, AcontecimentosService>();
            b._sp = services.BuildServiceProvider();
            b.Livro = new AcontecimentosService(b.Source, b._sp.GetServices<IAcontecimentoAssinante>(), NullLogger<AcontecimentosService>.Instance);
            b.Ouvinte = new GestaoTempoRealOuvinte(b.Source, b._sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<GestaoTempoRealOuvinte>.Instance);
            await b.Ouvinte.StartAsync(default);
            await Task.Delay(300); // LISTEN registrado
            return b;
        }

        /// <summary>Publica como um módulo faz (venda, caixa, estoque): na transação dele.</summary>
        public async Task<Guid> PublicarNaTransacao(PublicarAcontecimentoRequest r, bool commit, Guid? loja = null)
        {
            await using var c = await Source.OpenConnectionAsync();
            await using var tx = await c.BeginTransactionAsync();
            var p = await Livro.Publicar(c, tx, loja ?? Loja, 7, r, default);
            if (commit) await tx.CommitAsync(); else await tx.RollbackAsync();
            return p.Id;
        }

        public async Task<Recebido[]> Esperar(Guid loja, int quantos)
        {
            for (var i = 0; i < 50 && Painel.De(loja).Length < quantos; i++) await Task.Delay(100);
            await Task.Delay(300); // e nada a mais chega depois
            return Painel.De(loja);
        }

        public async ValueTask DisposeAsync()
        {
            await Ouvinte.StopAsync(default);
            await _sp.DisposeAsync();
            await Source.DisposeAsync();
            await using var admin = new NpgsqlConnection(_base); await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {_schema} CASCADE");
        }
    }

    private static PublicarAcontecimentoRequest Venda(string chave, decimal valor) => new()
    {
        ChaveIdempotencia = chave, Tipo = "venda", Modulo = ModuloGestao.Pedidos, DataFato = DateTimeOffset.UtcNow,
        Descricao = "Pedido 101", Origem = new OrigemRef { Tipo = "pedido", Id = chave },
        Lancamentos = new() { new LancamentoRequest { Natureza = Natureza.Receita, Valor = valor, Categoria = "vendas", Descricao = "Pedido 101" } },
    };

    [DeliveryDatabaseFact]
    public async Task Transacao_do_modulo_avisa_so_no_commit_e_uma_vez()
    {
        await using var db = await Banco.Criar();
        await db.PublicarNaTransacao(Venda("pedido:1", 50m), commit: false);
        Assert.Empty(await db.Esperar(db.Loja, 1)); // rollback: nada foi gravado, nada é anunciado

        var venda = Venda("pedido:2", 80m);
        var id = await db.PublicarNaTransacao(venda, commit: true);
        var avisos = await db.Esperar(db.Loja, 1);
        var aviso = Assert.Single(avisos);
        Assert.Equal(id, aviso.Id);
        Assert.Contains(GestaoTempoReal.Caixa, aviso.Recursos);
        Assert.Contains(GestaoTempoReal.Financeiro, aviso.Recursos);
        Assert.Empty(db.Painel.De(db.Outra)); // não vaza para outra loja

        await db.PublicarNaTransacao(venda, commit: true); // replay: mesma chave, nada novo
        Assert.Single(await db.Esperar(db.Loja, 2));
    }

    [DeliveryDatabaseFact]
    public async Task Transacao_propria_do_livro_avisa_em_processo_sem_repetir_pelo_canal()
    {
        await using var db = await Banco.Criar();
        var p = await db.Livro.Publicar(db.Loja, 7, Venda("pedido:3", 30m), default);
        var aviso = Assert.Single(await db.Esperar(db.Loja, 1));
        Assert.Equal(p.Id, aviso.Id);
    }

    [DeliveryDatabaseFact]
    public async Task Aviso_com_id_de_outra_loja_nao_vira_invalidacao()
    {
        await using var db = await Banco.Criar();
        var id = await db.PublicarNaTransacao(Venda("pedido:4", 10m), commit: true, loja: db.Outra);
        Assert.Single(await db.Esperar(db.Outra, 1));
        // Um aviso forjado com a loja errada é relido por ela e não acha o acontecimento.
        await db.Ouvinte.Processar($"{{\"L\":\"{db.Loja}\",\"Id\":\"{id}\",\"R\":null}}", default);
        Assert.Empty(db.Painel.De(db.Loja));
    }

    [DeliveryDatabaseFact]
    public async Task Aviso_fora_do_livro_sai_so_no_commit()
    {
        await using var db = await Banco.Criar();
        var id = Guid.NewGuid();
        await using (var c = await db.Source.OpenConnectionAsync())
        {
            await using (var tx = await c.BeginTransactionAsync())
            {
                await GestaoTempoReal.AvisarNoCommit(c, tx, db.Loja, id, GestaoTempoReal.Caixa);
                await tx.RollbackAsync();
            }
            Assert.Empty(await db.Esperar(db.Loja, 1));
            await using (var tx = await c.BeginTransactionAsync())
            {
                await GestaoTempoReal.AvisarNoCommit(c, tx, db.Loja, id, GestaoTempoReal.Caixa);
                await tx.CommitAsync();
            }
        }
        var aviso = Assert.Single(await db.Esperar(db.Loja, 1));
        Assert.Equal(new[] { GestaoTempoReal.Caixa }, aviso.Recursos);
    }
}

public class GestaoTempoRealTests
{
    private static AcontecimentoDto Dto(string modulo, int lancamentos = 0, int movimentos = 0) => new(Guid.NewGuid(), "x", modulo,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new DateOnly(2026, 11, 1), 7, null, null, null, "", default, null, null, null, null, null,
        Array.Empty<Guid>(),
        Enumerable.Range(0, lancamentos).Select(i => new LancamentoDto(Guid.NewGuid(), Guid.Empty, i, null, "receita", "entrada", 1, "pago", "ativo",
            new DateOnly(2026, 11, 1), null, null, 1, 1, null, null, null, null, null, false)).ToList(),
        Enumerable.Range(0, movimentos).Select(i => new MovimentoDto(Guid.NewGuid(), Guid.Empty, i, Guid.NewGuid(), "Queijo", 1, "kg", "saida", null,
            new DateOnly(2026, 11, 1), 7, DateTimeOffset.UtcNow, null)).ToList(),
        Array.Empty<LiquidacaoDto>());

    [Fact]
    public void Recursos_seguem_o_que_o_acontecimento_muda()
    {
        Assert.Equal(new[] { "financeiro", "caixa" }, GestaoTempoReal.RecursosDe(Dto(ModuloGestao.Pedidos, lancamentos: 1)));
        Assert.Equal(new[] { "estoque" }, GestaoTempoReal.RecursosDe(Dto(ModuloGestao.Estoque, movimentos: 2)));
        Assert.Equal(new[] { "caixa" }, GestaoTempoReal.RecursosDe(Dto(ModuloGestao.Caixa)));
        Assert.Equal(new[] { "financeiro", "caixa", "estoque" }, GestaoTempoReal.RecursosDe(Dto(ModuloGestao.Compras, lancamentos: 1, movimentos: 1)));
        Assert.Empty(GestaoTempoReal.RecursosDe(Dto(ModuloGestao.Curadoria)));
    }

    [Fact]
    public void Operador_de_caixa_nao_entra_no_grupo_do_financeiro()
    {
        static IEnumerable<string> Grupos(params (string, string)[] p) =>
            GestaoPapeis.Resolver((m, a) => p.Contains((m, a)), false).Areas.Intersect(GestaoTempoReal.Recursos);
        Assert.Equal(new[] { "caixa", "estoque" }, Grupos(("Caixa", "visualizar"), ("Estoque", "visualizar")));
        Assert.Equal(new[] { "caixa" }, Grupos(("Caixa", "visualizar")));
        Assert.Equal(new[] { "financeiro", "caixa", "estoque" }, Grupos(("Financeiro", "visualizar")));
        Assert.Empty(Grupos(("Delivery", "visualizar")));
        Assert.NotEqual(GestaoTempoReal.Grupo(Guid.NewGuid(), "caixa"), GestaoTempoReal.Grupo(Guid.NewGuid(), "caixa"));
    }

    [Fact]
    public void Payload_invalido_e_ignorado()
    {
        Assert.Null(GestaoTempoReal.Ler("nao e json"));
        Assert.Null(GestaoTempoReal.Ler("{\"L\":\"00000000-0000-0000-0000-000000000000\",\"Id\":\"00000000-0000-0000-0000-000000000000\"}"));
    }
}
