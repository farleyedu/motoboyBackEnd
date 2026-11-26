using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Social.Editor;
using APIBack.Social.Nucleo;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

/// <summary>
/// Vídeos gerados do Zippy Social (R13). O navegador ou a máquina gera; aqui só fica o registro e, para publicar, a URL do R2.
/// </summary>
[ApiController]
[Route("api/social/renders")]
[RequireModule(SocialPermissoes.Modulo)]
public sealed class SocialRendersController(ISocialRendersService renders) : ControllerBase
{
    [HttpPost]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Editar)]
    public Task<IActionResult> Criar([FromBody] NovoRenderRequest req) =>
        Run((loja, usuario, ct) => renders.CriarAsync(loja, usuario, req, ct));

    [HttpPatch("{id:guid}")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Editar)]
    public Task<IActionResult> Atualizar(Guid id, [FromBody] AtualizarRenderRequest req) =>
        Run((loja, _, ct) => renders.AtualizarAsync(loja, id, req, ct));

    [HttpGet]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Visualizar)]
    public Task<IActionResult> Listar([FromQuery] string? corteIds) =>
        Run((loja, _, ct) => renders.ListarAsync(loja, RendersRegras.Cortes(corteIds), ct));

    [HttpPost("{id:guid}/subir")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Editar)]
    public Task<IActionResult> Subir(Guid id, [FromBody] SubirRenderRequest req) =>
        Run((loja, _, ct) => renders.SubirAsync(loja, id, req, ct));

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
