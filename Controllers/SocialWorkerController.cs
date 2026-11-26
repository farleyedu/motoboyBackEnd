using APIBack.DTOs.Common;
using APIBack.Social.Fila;
using APIBack.Social.Nucleo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace APIBack.Controllers;

/// <summary>
/// Rotas do programa zippy-render (R14). Sem JWT: quem chama é a máquina, com o token dela em <c>X-Zippy-Maquina</c>
/// (só o pareamento vem sem token, com o código de 6 dígitos). Nada aqui redireciona.
/// </summary>
[ApiController]
[Route("api/social/worker")]
[AllowAnonymous]
public sealed class SocialWorkerController(ISocialWorkerService fila) : ControllerBase
{
    public const string Cabecalho = "X-Zippy-Maquina";
    public const string LimitePareamento = "social-pareamento";
    private static readonly TimeSpan Passo = TimeSpan.FromSeconds(2);

    [HttpPost("pareamento")]
    [EnableRateLimiting(LimitePareamento)]
    public Task<IActionResult> Parear([FromBody] ParearRequest req) =>
        Responder(async ct => Dados(await fila.ParearAsync(req, ct)));

    [HttpPost("sinal")]
    public Task<IActionResult> Sinal([FromBody] SinalRequest req) =>
        DaMaquina(async (maquina, ct) => Dados(await fila.SinalAsync(maquina, req, ct)));

    /// <summary>Long poll: segura até <paramref name="espera"/> segundos. 204 = nada para fazer.</summary>
    [HttpGet("trabalhos/proximo")]
    public Task<IActionResult> Proximo([FromQuery] int espera = FilaRegras.EsperaMaximaS) => DaMaquina(async (maquina, ct) =>
    {
        var fim = DateTime.UtcNow.AddSeconds(Math.Clamp(espera, 0, FilaRegras.EsperaMaximaS));
        while (true)
        {
            // o token é conferido de novo a cada volta: remover a máquina corta na hora
            var atual = await fila.AutenticarAsync(Token, ct);
            if (await fila.PegarAsync(atual, ct) is { } trabalho) return Dados(trabalho);
            var falta = fim - DateTime.UtcNow;
            if (falta <= TimeSpan.Zero) return NoContent();
            await Task.Delay(falta < Passo ? falta : Passo, ct);
        }
    });

    [HttpPost("trabalhos/{id:guid}/progresso")]
    public Task<IActionResult> Progresso(Guid id, [FromBody] ProgressoRequest req) =>
        DaMaquina(async (maquina, ct) => Dados(await fila.ProgressoAsync(maquina, id, req, ct)));

    [HttpPost("trabalhos/{id:guid}/concluir")]
    public Task<IActionResult> Concluir(Guid id, [FromBody] ConcluirRequest req) => DaMaquina(async (maquina, ct) =>
    {
        await fila.ConcluirAsync(maquina, id, req, ct);
        return Dados(new { });
    });

    [HttpPost("trabalhos/{id:guid}/falhar")]
    public Task<IActionResult> Falhar(Guid id, [FromBody] FalharRequest req) =>
        DaMaquina(async (maquina, ct) => Dados(await fila.FalharAsync(maquina, id, req, ct)));

    private string? Token => Request.Headers[Cabecalho].ToString();

    private IActionResult Dados(object valor) => Ok(ApiResponse<object>.Ok(valor));

    private Task<IActionResult> DaMaquina(Func<Maquina, CancellationToken, Task<IActionResult>> executar) =>
        Responder(async ct => await executar(await fila.AutenticarAsync(Token, ct), ct));

    private async Task<IActionResult> Responder(Func<CancellationToken, Task<IActionResult>> executar)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            return await executar(HttpContext.RequestAborted);
        }
        catch (SocialException ex)
        {
            return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo, ex.Detalhes));
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return NoContent();
        }
    }
}
