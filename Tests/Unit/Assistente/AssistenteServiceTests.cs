using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Assistente;
using APIBack.Repository.Interface;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit.Assistente;

public sealed class AssistenteServiceTests
{
    private static readonly Guid Loja = Guid.NewGuid();
    private static readonly DateTimeOffset Agora = new(2026, 11, 17, 15, 0, 0, TimeSpan.Zero);

    private sealed class Relogio(DateTimeOffset agora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => agora;
    }

    /// <summary>Ação de teste: conta execuções e devolve um desfazer.</summary>
    private sealed class AcaoFalsa(string nome, string? inversa = null) : IAcaoAssistente
    {
        public int Execucoes;
        public string Nome => nome;
        public string PermissaoModulo => "Cardapio";
        public string PermissaoAcao => "editar";
        public string? ModuloAtivo => null;
        public Task<AcaoPreparada> Preparar(ContextoAcao ctx, ParametrosAcao p, CancellationToken ct) =>
            Task.FromResult(new AcaoPreparada("Pausar X", "Disponível", "Pausado", new JsonObject { ["produtoId"] = "1" }));
        public Task<AcaoExecutada> Executar(ContextoAcao ctx, JsonObject parametros, CancellationToken ct)
        {
            Execucoes++;
            return Task.FromResult(new AcaoExecutada("Pronto.", inversa is null ? null
                : new Desfazer(inversa, new AcaoPreparada("Voltar X", "Pausado", "Disponível", new JsonObject()))));
        }
    }

    private const string CatalogoJson = """
        [
          { "id": "cardapio.pausar", "nome": "Pausar", "area": "Cardápio", "oQueFaz": "x", "comoAsPessoasPedem": ["pausar"],
            "modo": "ia_faz", "acao": "produto.pausar", "rota": "/apps/cardapio", "passos": ["a"], "permissao": "Cardapio:editar", "desfazer": "cardapio.voltar" },
          { "id": "cardapio.voltar", "nome": "Voltar", "area": "Cardápio", "oQueFaz": "x", "comoAsPessoasPedem": ["voltar"],
            "modo": "ia_faz", "acao": "produto.voltar", "rota": "/apps/cardapio", "passos": ["a"], "permissao": "Cardapio:editar", "desfazer": "cardapio.pausar" }
        ]
        """;

    private readonly Mock<IAssistenteRepositorio> _repo = new();
    private readonly AcaoFalsa _pausar = new("produto.pausar", "produto.voltar");
    private readonly AcaoFalsa _voltar = new("produto.voltar", "produto.pausar");

    private sealed class ProvedorFalso(string nome, bool chave) : IProvedorIA
    {
        public string Nome => nome;
        public bool TemChave => chave;
        public int Chamadas;
        public Exception? Falha;
        public Task<RespostaIA> Responder(PedidoIA pedido, ExecutarFerramenta executar, CancellationToken ct)
        {
            Chamadas++;
            return Falha is null ? Task.FromResult(new RespostaIA("ok", DesfechoIA.Normal, 10, 2, 0)) : Task.FromException<RespostaIA>(Falha);
        }
        public Task<IReadOnlyList<string>> Modelos(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    // Os testes do serviço usam uma cadeia só com a Anthropic (o provedor falso abaixo).
    private ConfigIA _cfg = ConfigIA.Padrao with { Cadeia = [new ItemCadeia("principal", Provedores.Anthropic, "claude-opus-5-5")] };
    private readonly ProvedorFalso _anthropic = new(Provedores.Anthropic, true);

    private AssistenteService Servico(bool comChave = true)
    {
        var cfgRepo = new Mock<IAssistenteConfigRepositorio>();
        cfgRepo.Setup(c => c.Obter(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ConfigIASalva(_cfg, false, null, null));
        return new(
            CatalogoAssistente.Carregar([("cardapio.json", CatalogoJson)]),
            [_pausar, _voltar],
            _repo.Object,
            Mock.Of<IOperacaoLojaService>(), Mock.Of<IHorarioOperacaoRepository>(), Mock.Of<ICardapioContractService>(), Mock.Of<IChamadosService>(),
            cfgRepo.Object,
            new CadeiaIA(new FabricaFalsa(comChave ? _anthropic : new ProvedorFalso(Provedores.Anthropic, false)), new EstadoVazio(),
                new Relogio(Agora), NullLogger<CadeiaIA>.Instance),
            NullLogger<AssistenteService>.Instance,
            new Relogio(Agora));
    }

    private sealed class FabricaFalsa(ProvedorFalso anthropic) : IFabricaProvedores
    {
        public IProvedorIA Criar(DefinicaoProvedor d) => d.Id == Provedores.Anthropic ? anthropic : new ProvedorFalso(d.Id, false);
        public bool TemChave(DefinicaoProvedor d) => d.Id == Provedores.Anthropic && anthropic.TemChave;
    }

    private sealed class EstadoVazio : IEstadoCadeiaRepositorio
    {
        public Task<FotoCadeia> Foto(DateTimeOffset inicioDoDiaUtc, CancellationToken ct) =>
            Task.FromResult(new FotoCadeia(new Dictionary<string, EstadoItem>(), new Dictionary<string, long>()));
        public Task MarcarEsgotado(ItemCadeia item, DateTimeOffset ate, string erro, CancellationToken ct) => Task.CompletedTask;
        public Task Desligar(ItemCadeia item, string erro, CancellationToken ct) => Task.CompletedTask;
        public Task Religar(string itemId, CancellationToken ct) => Task.CompletedTask;
        public Task RegistrarNaoSuporta(string provedor, string parametro, CancellationToken ct) => Task.CompletedTask;
        public Task LimparNaoSuporta(IReadOnlyCollection<string> provedores, CancellationToken ct) => Task.CompletedTask;
    }

    private static ContextoAcao Ctx(int usuario = 7, bool pode = true) => new(Loja, usuario, (_, _) => pode, ["Cardapio"]);

    private AcaoGuardada Pendente(Guid id, int usuario = 7, string situacao = "pendente", DateTimeOffset? expira = null) => new()
    {
        Id = id, EstabelecimentoId = Loja, UsuarioId = usuario, FuncionalidadeId = "cardapio.pausar", Acao = "produto.pausar",
        Parametros = "{\"produtoId\":\"1\"}", Resumo = "Pausar X", Situacao = situacao, ExpiraEm = expira ?? Agora.AddMinutes(5),
    };

    private static async Task<AssistenteException> Falha(Func<Task> f) => await Assert.ThrowsAsync<AssistenteException>(f);

    [Fact]
    public async Task ConfirmarExecutaAuditaEDeixaDesfazerPendente()
    {
        var id = Guid.NewGuid();
        _repo.Setup(r => r.ObterAcao(id, Loja, It.IsAny<CancellationToken>())).ReturnsAsync(Pendente(id));
        _repo.Setup(r => r.Decidir(id, "executando", 7, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        NovaAcao? desfazer = null;
        _repo.Setup(r => r.CriarAcao(It.IsAny<NovaAcao>(), It.IsAny<CancellationToken>())).Callback<NovaAcao, CancellationToken>((a, _) => desfazer = a);

        var r = await Servico().Confirmar(Ctx(), id, default);

        Assert.Equal("executada", r.Situacao);
        Assert.Equal(1, _pausar.Execucoes);
        _repo.Verify(x => x.Finalizar(id, "executada", null, It.IsAny<CancellationToken>()));
        _repo.Verify(x => x.Auditar(It.Is<AcaoGuardada>(a => a.Id == id), 7, "executada", "Pronto.", It.IsAny<CancellationToken>()));
        Assert.NotNull(desfazer);
        Assert.Equal(r.DesfazerAcaoId, desfazer!.Id);
        Assert.Equal("cardapio.voltar", desfazer.FuncionalidadeId);
        Assert.Equal(id, desfazer.DesfazAcaoId);
        Assert.Equal(Agora + AssistenteService.ValidadeAcao, desfazer.ExpiraEm);
    }

    [Fact]
    public async Task ConfirmarSemPermissaoNaoExecuta()
    {
        var id = Guid.NewGuid();
        _repo.Setup(r => r.ObterAcao(id, Loja, It.IsAny<CancellationToken>())).ReturnsAsync(Pendente(id));
        var ex = await Falha(() => Servico().Confirmar(Ctx(pode: false), id, default));
        Assert.Equal(403, ex.Status);
        Assert.Equal(0, _pausar.Execucoes);
        _repo.Verify(r => r.Decidir(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmarDeOutroUsuarioNaoAcha()
    {
        var id = Guid.NewGuid();
        _repo.Setup(r => r.ObterAcao(id, Loja, It.IsAny<CancellationToken>())).ReturnsAsync(Pendente(id, usuario: 99));
        Assert.Equal(404, (await Falha(() => Servico().Confirmar(Ctx(), id, default))).Status);
        Assert.Equal(0, _pausar.Execucoes);
    }

    [Fact]
    public async Task ConfirmarExpiradaDa410()
    {
        var id = Guid.NewGuid();
        _repo.Setup(r => r.ObterAcao(id, Loja, It.IsAny<CancellationToken>())).ReturnsAsync(Pendente(id, expira: Agora.AddSeconds(-1)));
        Assert.Equal(410, (await Falha(() => Servico().Confirmar(Ctx(), id, default))).Status);
    }

    [Fact]
    public async Task DuploCliqueSoExecutaUmaVez()
    {
        var id = Guid.NewGuid();
        _repo.Setup(r => r.ObterAcao(id, Loja, It.IsAny<CancellationToken>())).ReturnsAsync(Pendente(id));
        _repo.Setup(r => r.Decidir(id, "executando", 7, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        Assert.Equal(409, (await Falha(() => Servico().Confirmar(Ctx(), id, default))).Status);
        Assert.Equal(0, _pausar.Execucoes);
    }

    [Fact]
    public async Task JaDecididaDa409()
    {
        var id = Guid.NewGuid();
        _repo.Setup(r => r.ObterAcao(id, Loja, It.IsAny<CancellationToken>())).ReturnsAsync(Pendente(id, situacao: "executada"));
        Assert.Equal(409, (await Falha(() => Servico().Recusar(Ctx(), id, default))).Status);
    }

    [Fact]
    public async Task RecusarAuditaENaoExecuta()
    {
        var id = Guid.NewGuid();
        _repo.Setup(r => r.ObterAcao(id, Loja, It.IsAny<CancellationToken>())).ReturnsAsync(Pendente(id));
        _repo.Setup(r => r.Decidir(id, "recusada", 7, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var r = await Servico().Recusar(Ctx(), id, default);
        Assert.Equal("recusada", r.Situacao);
        Assert.Equal(0, _pausar.Execucoes);
        _repo.Verify(x => x.Auditar(It.IsAny<AcaoGuardada>(), 7, "recusada", null, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task SemChaveDa503()
    {
        var ex = await Falha(() => Servico(comChave: false).Enviar(Ctx(), new EnviarMensagemRequest { Texto = "oi" }, default));
        Assert.Equal(503, ex.Status);
        Assert.Equal("ASSISTENTE_INDISPONIVEL", ex.Codigo);
    }

    [Fact]
    public async Task DesligadoNaGestaoDa503()
    {
        _cfg = _cfg with { Ligado = false };
        Assert.Equal(503, (await Falha(() => Servico().Enviar(Ctx(), new EnviarMensagemRequest { Texto = "oi" }, default))).Status);
        Assert.Equal(0, _anthropic.Chamadas);
    }

    [Fact]
    public async Task EnviarGuardaProvedorModeloETokens()
    {
        UsoTokens? uso = null;
        _repo.Setup(r => r.GuardarMensagem(It.IsAny<Guid>(), It.IsAny<Guid>(), Loja, 7, "assistente", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<UsoTokens?>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, Guid, int, string, string, string, UsoTokens?, CancellationToken>((_, _, _, _, _, _, _, u, _) => uso = u);
        _cfg = _cfg with { LimiteDiario = 5 };
        var r = await Servico().Enviar(Ctx(), new EnviarMensagemRequest { Texto = "oi" }, default);
        Assert.Equal("ok", r.Texto);
        Assert.Equal(4, r.RestantesHoje);
        Assert.Equal(new UsoTokens(10, 2, 0, "claude-opus-5-5", Provedores.Anthropic, false, "principal"), uso);
        _repo.Verify(x => x.RegistrarUso(It.Is<RegistroUso>(u => u.Loja == Loja && u.Uso.Provedor == Provedores.Anthropic && u.Telas == 0),
            It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TextoVazioDa400(string texto)
    {
        Assert.Equal(400, (await Falha(() => Servico().Enviar(Ctx(), new EnviarMensagemRequest { Texto = texto }, default))).Status);
    }

    [Fact]
    public async Task LimiteDiarioDa429SemChamarAIa()
    {
        _repo.Setup(r => r.MensagensDaLojaDesde(Loja, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(ConfigIA.Padrao.LimiteDiario);
        var ex = await Falha(() => Servico().Enviar(Ctx(), new EnviarMensagemRequest { Texto = "oi" }, default));
        Assert.Equal(429, ex.Status);
        _repo.Verify(r => r.RegistrarEvento(Loja, null, 7, TiposEvento.Limite, null, null, AssistenteService.DiaLocal(Agora), It.IsAny<CancellationToken>()));
        _repo.Verify(r => r.GuardarMensagem(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<UsoTokens?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConversaVencidaDa404()
    {
        var ex = await Falha(() => Servico()
            .Enviar(Ctx(), new EnviarMensagemRequest { ConversaId = Guid.NewGuid(), Texto = "oi" }, default));
        Assert.Equal(404, ex.Status);
        _repo.Verify(r => r.ConversaExiste(It.IsAny<Guid>(), Loja, Agora - AssistenteService.GuardaConversa, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public void DiaComecaNaMeiaNoiteDeSaoPaulo()
    {
        // 02:30 UTC de 18/11 ainda é 23:30 de 17/11 em Brasília (UTC-3).
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 3, 0, 0, TimeSpan.Zero), AssistenteService.InicioDoDia(new DateTimeOffset(2026, 11, 18, 2, 30, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 3, 0, 0, TimeSpan.Zero), AssistenteService.InicioDoDia(new DateTimeOffset(2026, 11, 18, 3, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void CartoesSaemComTipoEVoltamMesmoComChavesReordenadas()
    {
        List<CartaoDto> cartoes =
        [
            new CartaoAcaoDto(Guid.NewGuid(), "cardapio.pausar", "Pausar", "Pausar X", "Disponível", "Pausado", Agora),
            new CartaoTelaDto("delivery.zonas", "Zonas", ["Abra Delivery"], "/apps/delivery"),
            new CartaoChamadoDto("ideia", "Título", "Texto"),
        ];
        var json = JsonSerializer.Serialize(cartoes, JsonAssistente.Opcoes);
        Assert.Contains("\"tipo\":\"acao\"", json);
        Assert.Contains("\"acaoId\"", json);

        // jsonb põe chave curta primeiro: "rota" antes de "tipo".
        var reordenado = """[{"rota":"/apps/delivery","tipo":"tela","passos":["Abra Delivery"],"titulo":"Zonas","funcionalidadeId":"delivery.zonas"}]""";
        var lido = Assert.IsType<CartaoTelaDto>(Assert.Single(JsonAssistente.LerCartoes(reordenado)));
        Assert.Equal("/apps/delivery", lido.Rota);
        Assert.Equal(3, JsonAssistente.LerCartoes(json).Count);
    }

    [Fact]
    public void HistoricoLembraOsCartoesMostrados()
    {
        var json = JsonSerializer.Serialize(new List<CartaoDto> { new CartaoChamadoDto("ideia", "Agendar pausa", "x") }, JsonAssistente.Opcoes);
        var texto = AssistenteService.TextoComCartoes("Ainda não tem.", json);
        Assert.StartsWith("Ainda não tem.", texto);
        Assert.Contains("rascunho de chamado mostrado: Agendar pausa", texto);
    }

    [Fact]
    public void PromptDeSistemaNaoMudaEntrePedidos()
    {
        var catalogo = CatalogoAssistente.Carregar([("cardapio.json", CatalogoJson)]);
        Assert.Equal(AssistenteService.MontarPrompt(catalogo.ParaPrompt(_ => true)), AssistenteService.MontarPrompt(catalogo.ParaPrompt(_ => true)));
        Assert.Contains("cardapio.pausar", AssistenteService.MontarPrompt(catalogo.ParaPrompt(_ => true)));
    }

    [Fact]
    public async Task TodosEsgotadosDaFraseAmigavelECartaoDeChamado()
    {
        _anthropic.Falha = new FalhaProvedorException(Provedores.Anthropic, "Limite de uso atingido", TipoFalha.Limite);
        var r = await Servico().Enviar(Ctx(), new EnviarMensagemRequest { Texto = "como mudo o horário?" }, default);
        Assert.Equal(AssistenteService.TextoEsgotado, r.Texto);
        var chamado = Assert.IsType<CartaoChamadoDto>(Assert.Single(r.Cartoes));
        Assert.Equal(("duvida", "como mudo o horário?"), (chamado.Categoria, chamado.Texto));
        _repo.Verify(x => x.RegistrarEvento(Loja, It.IsAny<Guid?>(), 7, TiposEvento.Erro, It.IsAny<string?>(), TiposEvento.CadeiaEsgotada,
            It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()));
        _repo.Verify(x => x.RegistrarUso(It.IsAny<RegistroUso>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
