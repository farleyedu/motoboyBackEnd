using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Social.Ia;
using APIBack.Social.Nucleo;
using APIBack.Social.Voz;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

/// <summary>
/// Voz e textos do Zippy Social (R10): estimativa e geração das falas, voz clonada com consentimento, roteiro, encurtar e ganchos.
/// Toda chamada a provedor sai daqui; nenhuma chave vai para o navegador. Exige o módulo Social e a permissão de editar.
/// </summary>
[ApiController]
[Route("api/social")]
[RequireModule(SocialPermissoes.Modulo)]
public sealed class SocialVozController(ISocialVozService voz, ISocialTextosService textos, SocialIa ia) : ControllerBase
{
    private const long CorpoClone = VozRegras.CloneMaxBytes + 256 * 1024;

    [HttpGet("creditos")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Visualizar)]
    public Task<IActionResult> Creditos() => Run(async (loja, _, ct) => SaldoDto.De(await ia.SaldoAsync(loja, ct)));

    [HttpPost("voz/estimativa")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Editar)]
    public Task<IActionResult> Estimativa([FromBody] EstimativaVozRequest req) =>
        Run((loja, _, ct) => voz.EstimativaAsync(loja, req, ct));

    [HttpPost("voz/gerar")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Editar)]
    public Task<IActionResult> Gerar([FromBody] GerarVozRequest req) =>
        Run((loja, usuario, ct) => voz.GerarAsync(loja, usuario, req, ct));

    [HttpGet("voz/clone")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Visualizar)]
    public Task<IActionResult> Clone() => Run((loja, usuario, ct) => voz.CloneAsync(loja, usuario, ct));

    /// <summary>Multipart: <c>audio</c> (60–90 s), <c>aceite=true</c>, <c>texto_lido</c> e, se o formato não disser, <c>duracao</c>.</summary>
    [HttpPost("voz/clone")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Editar)]
    [RequestSizeLimit(CorpoClone)]
    [RequestFormLimits(MultipartBodyLengthLimit = CorpoClone)]
    public Task<IActionResult> Clonar([FromForm] IFormFile? audio, [FromForm] string? aceite, [FromForm(Name = "texto_lido")] string? textoLido,
        [FromForm] decimal? duracao) =>
        Run(async (loja, usuario, ct) =>
        {
            if (audio is null || audio.Length == 0) throw SocialException.Validacao("Mande a gravação da sua voz.");
            if (audio.Length > VozRegras.CloneMaxBytes) throw SocialException.MuitoGrande("A gravação passa de 20 MB.");
            using var ms = new MemoryStream();
            await audio.CopyToAsync(ms, ct);
            var mime = (audio.ContentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
            var req = new ClonarVozRequest(ms.ToArray(), mime, audio.FileName, aceite is "true" or "1" or "sim", textoLido, duracao,
                HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString());
            return await voz.ClonarAsync(loja, usuario, req, ct);
        });

    [HttpDelete("voz/clone")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Editar)]
    public Task<IActionResult> ApagarClone() => Run((loja, usuario, ct) => voz.ApagarCloneAsync(loja, usuario, ct));

    [HttpPost("textos/roteiro")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Editar)]
    public Task<IActionResult> Roteiro([FromBody] RoteiroRequest req) => Run((loja, usuario, ct) => textos.RoteiroAsync(loja, usuario, req, ct));

    [HttpPost("textos/encurtar")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Editar)]
    public Task<IActionResult> Encurtar([FromBody] EncurtarRequest req) => Run((loja, usuario, ct) => textos.EncurtarAsync(loja, usuario, req, ct));

    [HttpPost("textos/ganchos")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Editar)]
    public Task<IActionResult> Ganchos([FromBody] GanchosRequest req) => Run((loja, usuario, ct) => textos.GanchosAsync(loja, usuario, req, ct));

    private async Task<IActionResult> Run<T>(Func<Guid, int, CancellationToken, Task<T>> executar)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetEstabelecimentoId() is not Guid loja || HttpContext.GetUserId() is not int usuario)
            return Unauthorized(ApiResponse<object>.Fail("Entre de novo.", "NAO_AUTENTICADO"));
        try
        {
            return Ok(ApiResponse<object>.Ok((await executar(loja, usuario, HttpContext.RequestAborted))!));
        }
        catch (SocialException ex)
        {
            return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo, ex.Detalhes));
        }
    }
}
