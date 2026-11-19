using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Assistente;
using APIBack.Atendimento;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace APIBack.Tests.Unit.Assistente;

public sealed class ChavesIATests
{
    private const string Mestra = "frase-mestra-de-teste-com-tamanho-qualquer";
    private const string ChaveGroq = "gsk_SEGREDO_super_secreto_1234a3F9";

    private static IConfiguration Config(Dictionary<string, string?>? extra = null)
    {
        var d = new Dictionary<string, string?> { ["Atendimento:ChaveToken"] = Mestra };
        foreach (var (k, v) in extra ?? []) d[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }

    private static readonly CifraChavesAssistente Cifra = new(Config());

    // ------------------------------------------------------------------ cifra

    [Fact]
    public void CifraEDecifra()
    {
        var c = Cifra.Proteger(ChaveGroq, "groq");
        Assert.StartsWith(CifraChavesAssistente.Prefixo, c);
        Assert.DoesNotContain(ChaveGroq, c);
        Assert.DoesNotContain("SEGREDO", c);
        Assert.Equal(ChaveGroq, Cifra.Revelar(c, "groq"));
        Assert.NotEqual(c, Cifra.Proteger(ChaveGroq, "groq")); // nonce novo a cada vez
    }

    [Fact]
    public void ChaveDeUmProvedorNaoDecifraComoDeOutro() =>
        Assert.Null(Cifra.Revelar(Cifra.Proteger(ChaveGroq, "groq"), "openai"));

    [Fact]
    public void OutraChaveMestraOuTextoMexidoNaoDecifra()
    {
        var c = Cifra.Proteger(ChaveGroq, "groq");
        var outra = new CifraChavesAssistente(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Atendimento:ChaveToken"] = "outra" }).Build());
        Assert.Null(outra.Revelar(c, "groq"));
        Assert.Null(Cifra.Revelar(c[..^4] + "AAAA", "groq"));
        Assert.Null(Cifra.Revelar("texto-puro", "groq"));
    }

    [Fact]
    public void SubchaveEhDiferenteDaDoWhatsApp()
    {
        // Mesmo segredo mestre, chaves de cifra diferentes: um não abre o do outro.
        var token = new TokenProtector(Config());
        var doWhatsApp = token.Proteger(ChaveGroq);
        Assert.Null(Cifra.Revelar(CifraChavesAssistente.Prefixo + doWhatsApp["v1.".Length..], "groq"));
        var doAssistente = Cifra.Proteger(ChaveGroq, "groq");
        Assert.Null(token.Revelar("v1." + doAssistente[CifraChavesAssistente.Prefixo.Length..]));
    }

    [Fact]
    public void SemChaveMestraNaoCifra()
    {
        var sem = new CifraChavesAssistente(new ConfigurationBuilder().Build());
        Assert.False(sem.Configurado);
        Assert.Throws<InvalidOperationException>(() => sem.Proteger(ChaveGroq, "groq"));
        Assert.Null(sem.Revelar(Cifra.Proteger(ChaveGroq, "groq"), "groq"));
    }

    [Fact]
    public void FinalSaoOsQuatroUltimos() => Assert.Equal("a3F9", CifraChavesAssistente.Final(ChaveGroq));

    // ------------------------------------------------------------------ ordem: ambiente > banco

    private static readonly DefinicaoProvedor Groq = ConfigIA.Padrao.ProvedorDe(Provedores.Groq)!;

    [Fact]
    public void AmbienteGanhaDoBanco()
    {
        var r = LeituraChave.Resolver(Groq, v => v == "GROQ_API_KEY" ? "do-ambiente" : null, _ => "do-banco");
        Assert.Equal(("do-ambiente", OrigensChave.Ambiente), r);
    }

    [Fact]
    public void SemAmbienteVaiNoBanco() =>
        Assert.Equal(("do-banco", OrigensChave.Banco), LeituraChave.Resolver(Groq, _ => null, _ => "do-banco"));

    [Fact]
    public void ProvedorSemVariavelSoUsaBanco()
    {
        var meu = Groq with { Id = "meu", VariavelChave = "" };
        Assert.Equal(("do-banco", OrigensChave.Banco), LeituraChave.Resolver(meu, _ => "nao-deve-ler", _ => "do-banco"));
        // E a configuração aceita provedor sem variável (chave só pela tela).
        Assert.Null((ConfigIA.Padrao with { Definicoes = [.. ConfigIA.Padrao.Definicoes, meu] }).Problema());
    }

    [Fact]
    public void VariavelProibidaNuncaELida()
    {
        var malicioso = Groq with { VariavelChave = "DATABASE_URL" };
        Assert.Equal((null, OrigensChave.Nenhuma), LeituraChave.Resolver(malicioso, _ => "postgres://segredo", _ => null));
    }

    [Fact]
    public void NenhumaChave() => Assert.Equal((null, OrigensChave.Nenhuma), LeituraChave.Resolver(Groq, _ => null, _ => null));

    // ------------------------------------------------------------------ cache de 1 minuto

    private sealed class RepoFalso : IChavesIARepositorio
    {
        public readonly Dictionary<string, ChaveGuardada> Linhas = [];
        public int Listagens;
        public readonly List<(string Provedor, string Cifrada, string Final)> Salvas = [];
        public Task<IReadOnlyList<ChaveGuardada>> Listar(CancellationToken ct) { Listagens++; return Task.FromResult<IReadOnlyList<ChaveGuardada>>(Linhas.Values.ToList()); }
        public Task<string> Salvar(string provedor, string cifrada, string final, int usuario, CancellationToken ct)
        {
            var acao = Linhas.ContainsKey(provedor) ? "trocou" : "cadastrou";
            Salvas.Add((provedor, cifrada, final));
            Linhas[provedor] = new ChaveGuardada(provedor, cifrada, final, "Fulano", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, true, null);
            return Task.FromResult(acao);
        }
        public Task<bool> Apagar(string provedor, int usuario, CancellationToken ct) => Task.FromResult(Linhas.Remove(provedor));
    }

    private sealed class Relogio : TimeProvider
    {
        public DateTimeOffset Agora = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Agora;
    }

    /// <summary>Guarda tudo que foi logado, para conferir que a chave nunca aparece.</summary>
    private sealed class LogGuardado : ILoggerProvider, ILogger
    {
        public readonly ConcurrentBag<string> Linhas = [];
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Linhas.Add(formatter(state, exception) + " " + exception);
        public void Dispose() { }
    }

    private static (ChavesIA Cache, RepoFalso Repo, Relogio Relogio, LogGuardado Log) Cache()
    {
        var repo = new RepoFalso();
        var sp = new ServiceCollection().AddSingleton<IChavesIARepositorio>(repo).BuildServiceProvider();
        var relogio = new Relogio();
        var log = new LogGuardado();
        return (new ChavesIA(sp.GetRequiredService<IServiceScopeFactory>(), Cifra, relogio, new LoggerFactory([log]).CreateLogger<ChavesIA>()), repo, relogio, log);
    }

    [Fact]
    public async Task CacheLeDoBancoUmaVezPorMinutoEInvalidaAoSalvar()
    {
        var (cache, repo, relogio, _) = Cache();
        repo.Linhas["groq"] = new ChaveGuardada("groq", Cifra.Proteger(ChaveGroq, "groq"), "a3F9", null, DateTimeOffset.UnixEpoch, null, null, null);
        await cache.Garantir(default);
        await cache.Garantir(default);
        Assert.Equal(ChaveGroq, cache.DoBanco("groq"));
        Assert.Equal(1, repo.Listagens);
        relogio.Agora = relogio.Agora.AddSeconds(61);
        await cache.Garantir(default);
        Assert.Equal(2, repo.Listagens);
        cache.Invalidar();
        await cache.Garantir(default);
        Assert.Equal(3, repo.Listagens);
    }

    [Fact]
    public async Task ChaveQueNaoDecifraFicaDeForaSemVazar()
    {
        var (cache, repo, _, log) = Cache();
        repo.Linhas["groq"] = new ChaveGuardada("groq", "ak1.lixo", "a3F9", null, DateTimeOffset.UnixEpoch, null, null, null);
        await cache.Garantir(default);
        Assert.Null(cache.DoBanco("groq"));
        Assert.Contains(log.Linhas, l => l.Contains("não decifra"));
    }

    // ------------------------------------------------------------------ cadastrar pela Gestão

    private sealed class Testador(FalhaProvedorException? falha = null) : ITestadorChave
    {
        public readonly List<(string Provedor, string Chave, string? Modelo)> Testes = [];
        public Task Testar(DefinicaoProvedor definicao, string chave, string? modelo, CancellationToken ct)
        {
            Testes.Add((definicao.Id, chave, modelo));
            return falha is null ? Task.CompletedTask : Task.FromException(falha);
        }
    }

    private sealed class EstadoFalso : IEstadoCadeiaRepositorio
    {
        public readonly List<string> Religados = [];
        public Task<FotoCadeia> Foto(DateTimeOffset i, CancellationToken ct) => Task.FromResult(FotoCadeia.Vazia);
        public Task MarcarEsgotado(ItemCadeia item, DateTimeOffset ate, string erro, CancellationToken ct) => Task.CompletedTask;
        public Task Desligar(ItemCadeia item, string erro, CancellationToken ct) => Task.CompletedTask;
        public Task Religar(string itemId, CancellationToken ct) { Religados.Add(itemId); return Task.CompletedTask; }
        public Task RegistrarNaoSuporta(string provedor, string parametro, CancellationToken ct) => Task.CompletedTask;
        public Task LimparNaoSuporta(IReadOnlyCollection<string> provedores, CancellationToken ct) => Task.CompletedTask;
    }

    private static (GestaoChavesIA Gestao, RepoFalso Repo, Testador Testador, EstadoFalso Estado, LogGuardado Log, FabricaProvedores Fabrica) Gestao(
        FalhaProvedorException? falha = null, Dictionary<string, string?>? ambiente = null, CifraChavesAssistente? cifra = null)
    {
        var (cache, repo, _, log) = Cache();
        var fabrica = new FabricaProvedores(Config(ambiente), cache, new LoggerFactory([log]));
        var testador = new Testador(falha);
        var estado = new EstadoFalso();
        return (new GestaoChavesIA(repo, cifra ?? Cifra, cache, testador, fabrica, estado, new LoggerFactory([log]).CreateLogger<GestaoChavesIA>()),
            repo, testador, estado, log, fabrica);
    }

    [Fact]
    public async Task TesteOkGravaCifradaEDevolveSoOFinal()
    {
        var (g, repo, testador, estado, log, fabrica) = Gestao();
        var r = await g.Salvar(ConfigIA.Padrao, "groq", "  " + ChaveGroq + " ", 7, default);
        Assert.Equal(new ResultadoChaveDto("groq", true, true, "a3F9", OrigensChave.Banco, null), r);
        var salva = Assert.Single(repo.Salvas);
        Assert.DoesNotContain(ChaveGroq, salva.Cifrada);
        Assert.Equal(ChaveGroq, Cifra.Revelar(salva.Cifrada, "groq"));
        Assert.Equal(("groq", ChaveGroq, "llama-3.3-70b-versatile"), Assert.Single(testador.Testes));
        Assert.Equal(["groq"], estado.Religados);
        Assert.True(fabrica.TemChave(Groq));
        Assert.DoesNotContain(ChaveGroq, JsonSerializer.Serialize(r));
        Assert.DoesNotContain(log.Linhas, l => l.Contains(ChaveGroq) || l.Contains("SEGREDO"));
    }

    [Fact]
    public async Task TesteFalhoNaoGrava()
    {
        var (g, repo, _, _, log, _) = Gestao(new FalhaProvedorException("groq", MensagensFalha.ChaveRecusada, TipoFalha.Configuracao, status: 401));
        var r = await g.Salvar(ConfigIA.Padrao, "groq", ChaveGroq, 7, default);
        Assert.Equal(new ResultadoChaveDto("groq", false, false, null, OrigensChave.Nenhuma, "Chave recusada"), r);
        Assert.Empty(repo.Salvas);
        Assert.DoesNotContain(log.Linhas, l => l.Contains(ChaveGroq));
    }

    [Fact]
    public async Task ChaveNoAmbienteContinuaGanhando()
    {
        var (g, _, _, _, _, fabrica) = Gestao(ambiente: new() { ["GROQ_API_KEY"] = "gsk_do_ambiente_9xQ2" });
        var r = await g.Salvar(ConfigIA.Padrao, "groq", ChaveGroq, 7, default);
        Assert.Equal((true, OrigensChave.Ambiente), (r.Gravada, r.Origem));
        Assert.Equal((true, OrigensChave.Ambiente, "9xQ2"), fabrica.Descrever(Groq));
    }

    [Theory]
    [InlineData("curta")]
    [InlineData("tem espaco no meio")]
    [InlineData("")]
    public async Task ChaveMalFormadaNemTesta(string chave)
    {
        var (g, repo, testador, _, _, _) = Gestao();
        Assert.Equal("CHAVE_INVALIDA", (await Assert.ThrowsAsync<AssistenteException>(() => g.Salvar(ConfigIA.Padrao, "groq", chave, 7, default))).Codigo);
        Assert.Empty(testador.Testes);
        Assert.Empty(repo.Salvas);
    }

    [Fact]
    public async Task SemCifraNaoDeixaSalvar()
    {
        var (g, _, testador, _, _, _) = Gestao(cifra: new CifraChavesAssistente(new ConfigurationBuilder().Build()));
        Assert.Equal("CIFRA_INDISPONIVEL", (await Assert.ThrowsAsync<AssistenteException>(() => g.Salvar(ConfigIA.Padrao, "groq", ChaveGroq, 7, default))).Codigo);
        Assert.Empty(testador.Testes);
    }

    [Fact]
    public async Task ProvedorForaDaConfiguracaoDa404()
    {
        var (g, _, _, _, _, _) = Gestao();
        Assert.Equal(404, (await Assert.ThrowsAsync<AssistenteException>(() => g.Salvar(ConfigIA.Padrao, "nao-existe", ChaveGroq, 7, default))).Status);
    }

    [Fact]
    public async Task InfoNuncaTrazOValor()
    {
        var (g, _, _, _, _, _) = Gestao();
        await g.Salvar(ConfigIA.Padrao, "groq", ChaveGroq, 7, default);
        var info = await g.Info(ConfigIA.Padrao, default);
        Assert.Equal((true, OrigensChave.Banco, "a3F9", "Fulano"), (info["groq"].TemChave, info["groq"].Origem, info["groq"].Final, info["groq"].SalvaPor));
        Assert.Equal((false, OrigensChave.Nenhuma), (info["anthropic"].TemChave, info["anthropic"].Origem));
        Assert.DoesNotContain(ChaveGroq, JsonSerializer.Serialize(info));
    }

    [Fact]
    public async Task ApagarTiraDoBancoEDaMemoria()
    {
        var (g, _, _, _, _, fabrica) = Gestao();
        await g.Salvar(ConfigIA.Padrao, "groq", ChaveGroq, 7, default);
        Assert.Equal(new ChaveApagadaDto("groq", true), await g.Apagar("groq", 7, default));
        await g.Info(ConfigIA.Padrao, default); // recarrega o cache invalidado
        Assert.False(fabrica.TemChave(Groq));
        Assert.Equal(new ChaveApagadaDto("groq", false), await g.Apagar("groq", 7, default));
    }
}
