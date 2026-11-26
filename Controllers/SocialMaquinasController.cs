using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Social.Fila;
using APIBack.Social.Nucleo;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

/// <summary>
/// Máquinas com o zippy-render, vistas pelo painel (R14): código de pareamento, a minha lista (e as compartilhadas comigo),
/// renomear, compartilhar, desativar e remover.
/// </summary>
[ApiController]
[Route("api/social/maquinas")]
[RequireModule(SocialPermissoes.Modulo)]
public sealed class SocialMaquinasController(ISocialMaquinasService maquinas) : ControllerBase
{
    [HttpPost("pareamento")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Editar)]
    public Task<IActionResult> NovoCodigo() => Run((loja, usuario, ct) => maquinas.NovoCodigoAsync(loja, usuario, ct));

    [HttpGet]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Visualizar)]
    public Task<IActionResult> Listar() => Run((loja, usuario, ct) => maquinas.ListarAsync(loja, usuario, ct));

    [HttpPatch("{id:long}")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Editar)]
    public Task<IActionResult> Mudar(long id, [FromBody] MudarMaquinaRequest req) =>
        Run((loja, usuario, ct) => maquinas.MudarAsync(loja, usuario, id, req, ct));

    [HttpDelete("{id:long}")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Editar)]
    public Task<IActionResult> Remover(long id) => Run<object>(async (loja, usuario, ct) =>
    {
        await maquinas.RemoverAsync(loja, usuario, id, ct);
        return new { };
    });

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
