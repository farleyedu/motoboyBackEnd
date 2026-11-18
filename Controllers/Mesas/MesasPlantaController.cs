using APIBack.Mesas.Nucleo;
using APIBack.Mesas.Planta;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers.Mesas;

/// <summary>
/// Planta e salão (CONTRATO-API §3.3 e §4.1). Rascunho, histórico e publicar: dono e gerente (<c>Mesas.configurar</c>), mesmo com o
/// Mesas desligado (a loja monta o salão antes de ligar). Planta publicada e leitura do app: toda a equipe (<c>Mesas.ver</c>).
/// Bloquear e liberar mesa: <c>Mesas.gerir</c>.
/// </summary>
[Route("api/v2/mesas")]
public sealed class MesasPlantaController(IMesasNucleoService nucleo, IPlantaService planta) : MesasControllerBase(nucleo)
{
    /// <summary>Resposta sem planta ainda: <c>dados = null</c> (o painel mostra o estado vazio).</summary>
    private static readonly object Nada = null!;

    /// <summary><c>?estado=publicada</c> (padrão) ou <c>rascunho</c>; <c>?versao=n</c> lê uma versão do histórico. Sem planta: <c>dados = null</c>.</summary>
    [HttpGet("planta")]
    public Task<IActionResult> Ler([FromQuery] string? estado, [FromQuery] int? versao)
    {
        var publicada = versao is null && (string.IsNullOrWhiteSpace(estado) || estado == "publicada");
        if (!publicada && versao is null && estado != "rascunho")
            return Task.FromResult<IActionResult>(BadRequest(DTOs.Common.ApiResponse<object>.Fail("Use estado=publicada ou rascunho.", "VALIDACAO")));
        return Run(publicada ? MesasPermissoes.Ver : MesasPermissoes.Configurar, async ctx =>
            await planta.LerAsync(ctx.Loja, publicada ? "publicada" : "rascunho", versao, HttpContext.RequestAborted) ?? Nada,
            gestorPassaDesligado: true);
    }

    [HttpGet("planta/versoes")]
    public Task<IActionResult> Versoes() => Run(MesasPermissoes.Configurar, async ctx =>
        await planta.VersoesAsync(ctx.Loja, HttpContext.RequestAborted), gestorPassaDesligado: true);

    /// <summary>Planta publicada sem foto de fundo e as mesas ativas com estado e versão (app do garçom). Sem planta: <c>dados = null</c>.</summary>
    [HttpGet("planta/app")]
    public Task<IActionResult> App() => Run(MesasPermissoes.Ver, async ctx =>
        await planta.AppAsync(ctx.Loja, HttpContext.RequestAborted) ?? Nada, gestorPassaDesligado: true);

    [HttpPost("planta/salvar-rascunho-planta")]
    public Task<IActionResult> SalvarRascunho([FromBody] SalvarRascunhoPlantaRequest? req) => Run(MesasPermissoes.Configurar, async ctx =>
        await planta.SalvarRascunhoAsync(ctx, req ?? new SalvarRascunhoPlantaRequest(null, null, null), HttpContext.RequestAborted),
        gestorPassaDesligado: true);

    /// <summary>Com problema na planta: <c>VALIDACAO</c> com <c>details.erros[] {item, id, codigo, mensagem}</c> e nada muda.</summary>
    [HttpPost("planta/publicar-planta")]
    public Task<IActionResult> Publicar([FromBody] PublicarPlantaRequest? req) => Run(MesasPermissoes.Configurar, async ctx =>
        await planta.PublicarAsync(ctx, req ?? new PublicarPlantaRequest(null, null), HttpContext.RequestAborted),
        gestorPassaDesligado: true);

    [HttpPost("planta/descartar-rascunho-planta")]
    public Task<IActionResult> Descartar([FromBody] DescartarRascunhoPlantaRequest? req) => Run(MesasPermissoes.Configurar, async ctx =>
        await planta.DescartarAsync(ctx, req ?? new DescartarRascunhoPlantaRequest(null, null), HttpContext.RequestAborted),
        gestorPassaDesligado: true);

    [HttpPost("salao/bloquear-mesa")]
    public Task<IActionResult> Bloquear([FromBody] MesaBloqueioRequest? req) => Run(MesasPermissoes.Gerir, async ctx =>
        await planta.BloquearAsync(ctx, req ?? new MesaBloqueioRequest(null, null, null, null), true, HttpContext.RequestAborted));

    [HttpPost("salao/desbloquear-mesa")]
    public Task<IActionResult> Desbloquear([FromBody] MesaBloqueioRequest? req) => Run(MesasPermissoes.Gerir, async ctx =>
        await planta.BloquearAsync(ctx, req ?? new MesaBloqueioRequest(null, null, null, null), false, HttpContext.RequestAborted));
}
