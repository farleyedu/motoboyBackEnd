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
    CacheModelosIA modelos, IAssistenteUsoRepositorio uso, TimeProvider relogio) : GestaoControllerBase
{
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
        return new ConfigIADto(cfg, salva.Padrao, salva.AtualizadoPor, salva.AtualizadoEm,
            cfg.Definicoes.ToDictionary(d => d.Id, cadeia.TemChave),
            listas.ToDictionary(l => l.Id, l => l.Lista),
            SemPreco(cfg, await uso.ModelosUsados(ct)),
            cfg.Cadeia.Select(i => RegrasCadeia.Disponibilidade(cfg, i, foto, cadeia.TemChave, agora))
                .Select(d => new EstadoItemDto(d.Item.Id, d.Disponivel, d.Motivo, d.EsgotadoAte, d.Desligado, d.Erro, d.UsadasHoje, d.TemChave)).ToList(),
            AlertasCadeia.Calcular(cfg, foto, cadeia.TemChave, agora, esgotada.Vezes, esgotada.Primeira),
            foto.NaoSuporta ?? new Dictionary<string, IReadOnlySet<string>>());
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
