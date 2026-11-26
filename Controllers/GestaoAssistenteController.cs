using APIBack.Assistente;
using APIBack.DTOs.Common;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

/// <summary>
/// Gestão da plataforma: quais IAs o Assistente usa, em que ordem, e quanto gastam (CONTRATO-API §7, §8 e §10). Só super admin.
/// Chaves ficam em variável de ambiente; aqui só se diz se cada uma existe.
/// </summary>
[ApiController]
[Route("api/gestao/assistente")]
public sealed class GestaoAssistenteController(
    IAssistenteConfigRepositorio configuracao, IAssistenteService assistente, CadeiaIA cadeia, IEstadoCadeiaRepositorio estado,
    CacheModelosIA modelos, IAssistenteUsoRepositorio uso, TimeProvider relogio, GestaoChavesIA chaves, ChavesIA cacheChaves,
    DiagnosticoIA diagnostico) : GestaoControllerBase
{
    /// <summary>Modelos que a chave enxerga, com tipo e recomendados (CONTRATO-API §12.4). A chave colada não é gravada.</summary>
    [HttpPost("provedores/{id}/modelos")]
    public Task<IActionResult> ListarModelos(string id, [FromBody] ListarModelosRequest? req) => Run(async (_, ct) =>
    {
        await cacheChaves.Garantir(ct);
        return await diagnostico.ListarModelos((await configuracao.Obter(ct)).Config, id, Vazio(req?.Chave), ct);
    });

    /// <summary>Etapas do provedor + modelo, parando na primeira que falhar (CONTRATO-API §12.5).</summary>
    [HttpPost("provedores/{id}/diagnosticar")]
    public Task<IActionResult> Diagnosticar(string id, [FromBody] DiagnosticarRequest? req) => Run(async (_, ct) =>
    {
        await cacheChaves.Garantir(ct);
        return await diagnostico.Diagnosticar((await configuracao.Obter(ct)).Config, id, req?.Modelo, Vazio(req?.Chave), req?.Ferramentas == true, ct);
    });

    /// <summary>"Verificar tudo": o diagnóstico de cada item ligado (CONTRATO-API §12.7).</summary>
    [HttpPost("diagnosticar")]
    public Task<IActionResult> VerificarTudo() => Run(async (_, ct) =>
    {
        await cacheChaves.Garantir(ct);
        return await diagnostico.VerificarTudo((await configuracao.Obter(ct)).Config, ct);
    });

    private static string? Vazio(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;

    /// <summary>Cadastra ou troca a chave de um provedor (CONTRATO-API §11.2). Testa antes; só grava se der certo. Nunca devolve a chave.</summary>
    [HttpPut("provedores/{id}/chave")]
    public Task<IActionResult> SalvarChave(string id, [FromBody] SalvarChaveRequest? req) => Run(async (usuario, ct) =>
        await chaves.Salvar((await configuracao.Obter(ct)).Config, id, req?.Chave, usuario, ct));

    [HttpDelete("provedores/{id}/chave")]
    public Task<IActionResult> ApagarChave(string id) => Run((usuario, ct) => chaves.Apagar(id, usuario, ct));

    [HttpGet("config")]
    public Task<IActionResult> Ler() => Run((_, ct) => Montar(ct));

    [HttpPut("config")]
    public Task<IActionResult> Salvar([FromBody] ConfigIA? cfg) => Run(async (usuario, ct) =>
    {
        var valida = Validar(cfg);
        var antes = (await configuracao.Obter(ct)).Config;
        await configuracao.Salvar(valida, usuario, ct);
        await estado.LimparNaoSuporta(AlertasCadeia.ProvedoresMudados(antes, valida), ct);
        return await Montar(ct);
    });

    [HttpPost("cadeia/{itemId}/religar")]
    public Task<IActionResult> Religar(string itemId) => Run(async (_, ct) =>
    {
        await estado.Religar(itemId, ct);
        return await Montar(ct);
    });

    [HttpPost("testar")]
    public Task<IActionResult> Testar([FromBody] TestarIARequest? req) =>
        Run((_, ct) => assistente.Testar(Validar(req?.Config), string.IsNullOrWhiteSpace(req?.ItemId) ? null : req.ItemId.Trim(),
            req?.Ferramentas == true, req?.Pergunta, ct));

    /// <summary>Uso e custo do período (CONTRATO-API §8.2 e §10.4). Dias no fuso de São Paulo; padrão: últimos 30 dias.</summary>
    [HttpGet("uso")]
    public Task<IActionResult> Uso([FromQuery] string? de, [FromQuery] string? ate) => Run(async (_, ct) =>
    {
        var agora = relogio.GetUtcNow();
        var (inicio, fim) = UsoCalculo.Periodo(de, ate, AssistenteService.DiaLocal(agora));
        var dados = await uso.Ler(AssistenteService.InicioDoDia(inicio), AssistenteService.InicioDoDia(fim.AddDays(1)), agora, ct);
        var cfg = (await configuracao.Obter(ct)).Config;
        return UsoCalculo.Montar(inicio, fim, dados, cfg, await cadeia.Foto(ct), agora);
    });

    private static ConfigIA Validar(ConfigIA? cfg)
    {
        if (cfg is null) throw new AssistenteException(422, "CONFIG_INVALIDA", "Envie a configuração.");
        var n = cfg.Normalizada();
        return n.Problema() is string problema ? throw new AssistenteException(422, "CONFIG_INVALIDA", problema) : n;
    }

    private async Task<ConfigIADto> Montar(CancellationToken ct)
    {
        var salva = await configuracao.Obter(ct);
        var cfg = salva.Config;
        var listas = await Task.WhenAll(cfg.Definicoes.Select(async d => (d.Id, Lista: await modelos.Modelos(d, ct))));
        var foto = await cadeia.Foto(ct);
        var agora = relogio.GetUtcNow();
        var esgotada = await uso.CadeiaEsgotada(AssistenteService.InicioDoDia(agora), ct);
        var porProvedor = listas.ToDictionary(l => l.Id, l => l.Lista);
        var estadoItens = cfg.Cadeia.Select(i => RegrasCadeia.Disponibilidade(cfg, i, foto, cadeia.TemChave, agora))
            .Select(d => EstadoComFalha(cfg, d, foto, porProvedor)).ToList();
        var alertas = AlertasCadeia.Calcular(cfg, foto, cadeia.TemChave, agora, esgotada.Vezes, esgotada.Primeira)
            .Select(a => ComAcoes(a, estadoItens)).ToList();
        return new ConfigIADto(cfg, salva.Padrao, salva.AtualizadoPor, salva.AtualizadoEm, cacheChaves.CifraDisponivel,
            await chaves.Info(cfg, ct),
            porProvedor,
            SemPreco(cfg, await uso.ModelosUsados(ct)),
            estadoItens,
            alertas,
            foto.NaoSuporta ?? new Dictionary<string, IReadOnlySet<string>>(),
            Saude(cfg, estadoItens));
    }

    /// <summary>Estado do item com a última falha explicada (e modelos para trocar). Sem falha guardada, monta uma pelo motivo. Público para teste.</summary>
    public static EstadoItemDto EstadoComFalha(ConfigIA cfg, DisponibilidadeItem d, FotoCadeia foto, IReadOnlyDictionary<string, IReadOnlyList<string>> modelos)
    {
        var guardado = RegrasCadeia.EstadoValido(d.Item, foto);
        var def = cfg.ProvedorDe(d.Item.Provedor);
        // Só item parado por falha leva a falha: a guardada de ontem não pode aparecer num item que voltou.
        var falha = d.Motivo switch
        {
            MotivosPulo.DesligadoErro or MotivosPulo.Esgotado => guardado?.Falha,
            MotivosPulo.SemChave when def is not null => CatalogoFalhas.Dto(CodigosFalha.SemChave, def.Id, def.Nome, d.Item.Modelo, EtapasFalha.FormatoChave),
            _ => null,
        };
        if (falha is not null && falha.Acoes.Contains(AcoesFalha.TrocarModelo) && falha.ModelosSugeridos is null
            && modelos.TryGetValue(d.Item.Provedor, out var lista) && lista.Count > 0)
        {
            var sug = CatalogoModelos.Sugeridos(d.Item.Provedor, lista, d.Item.Modelo);
            if (sug.Count > 0) falha = falha with { ModelosSugeridos = sug };
        }
        // Item desligado por erro: depois de resolver, dá para religar.
        if (falha is not null && d.Desligado && !falha.Acoes.Contains(AcoesFalha.Religar))
            falha = falha with { Acoes = [.. falha.Acoes, AcoesFalha.Religar] };
        return new EstadoItemDto(d.Item.Id, d.Disponivel, d.Motivo, d.EsgotadoAte, d.Desligado, falha?.Titulo ?? d.Erro, d.UsadasHoje, d.TemChave,
            falha, falha is null || d.Motivo == MotivosPulo.SemChave ? null : guardado?.FalhaEm);
    }

    /// <summary>Aviso do topo com código e ações (§12.6). Público para teste.</summary>
    public static AlertaDto ComAcoes(AlertaDto a, IReadOnlyList<EstadoItemDto> estado)
    {
        var item = a.ItemId is null ? null : estado.FirstOrDefault(e => e.ItemId == a.ItemId);
        return a.Tipo switch
        {
            AlertasCadeia.ItemDesligado => a with { Codigo = item?.Falha?.Codigo, Acoes = item?.Falha?.Acoes ?? [AcoesFalha.Religar] },
            AlertasCadeia.SemChave => a with { Codigo = CodigosFalha.SemChave, Acoes = [AcoesFalha.ColarChave] },
            AlertasCadeia.GratisEsgotados => a with { Codigo = CodigosFalha.CotaDia, Acoes = [AcoesFalha.TrocarProvedor] },
            _ => a,
        };
    }

    /// <summary>Resumo do topo (§12.7): parado = nenhum item ligado disponível; atenção = algum ligado com problema. Público para teste.</summary>
    public static SaudeDto Saude(ConfigIA cfg, IReadOnlyList<EstadoItemDto> estado)
    {
        var ligados = cfg.Cadeia.Where(i => i.Ligado).Select(i => i.Id).ToHashSet();
        var dosLigados = estado.Where(e => ligados.Contains(e.ItemId)).ToList();
        if (!cfg.Ligado) return new SaudeDto("parado", "Assistente desligado", []);
        if (dosLigados.Count == 0) return new SaudeDto("parado", "Nenhuma IA ligada", []);
        var problema = dosLigados.Where(e => !e.Disponivel).Select(e => e.ItemId).ToList();
        if (problema.Count == dosLigados.Count) return new SaudeDto("parado", "Nenhuma IA respondendo", problema);
        if (problema.Count > 0) return new SaudeDto("atencao", problema.Count == 1 ? "1 IA precisa de atenção" : $"{problema.Count} IAs precisam de atenção", problema);
        return new SaudeDto("ok", "Tudo funcionando", []);
    }

    /// <summary>Modelos usados ou da cadeia que não têm preço (itens de provedor grátis não precisam). Público para teste.</summary>
    public static IReadOnlyList<string> SemPreco(ConfigIA cfg, IEnumerable<(string Provedor, string Modelo)> usados) =>
        usados.Concat(cfg.Cadeia.Select(i => (i.Provedor, i.Modelo)))
            .Where(u => UsoCalculo.PrecoEfetivo(cfg, u.Provedor, u.Modelo) is null).Select(u => u.Modelo)
            .Distinct().OrderBy(m => m, StringComparer.Ordinal).ToList();

    private async Task<IActionResult> Run<T>(Func<int, CancellationToken, Task<T>> acao)
    {
        if (!CurrentUserId.HasValue) return UnauthorizedResponse();
        if (!CurrentIsSuperAdmin) return ForbiddenResponse();
        Response.Headers.CacheControl = "no-store";
        try
        {
            return Ok(ApiResponse<T>.Ok(await acao(CurrentUserId.Value, HttpContext.RequestAborted)));
        }
        catch (AssistenteException ex)
        {
            return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo));
        }
    }
}
