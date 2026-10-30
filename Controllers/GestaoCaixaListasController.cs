using APIBack.Acontecimentos;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.GestaoLogica.CaixaListas;
using APIBack.Security;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

/// <summary>
/// Listas do Caixa por loja (6C2-C): motivos de sangria, suprimento, pagamento e devolução, e justificativas de fechamento.
/// Gerente e operador de caixa consultam (o operador, só as ativas); só o gerente inclui, renomeia e desativa.
/// </summary>
[ApiController]
[Route("api/gestao/caixa/listas")]
public sealed class GestaoCaixaListasController(ICaixaListasService service) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> Listar(CancellationToken ct) => Run(false, async (loja, _, acesso) =>
        await service.Listar(loja, acesso.Papel == GestaoPapeis.Gerente, ct));

    [HttpPost]
    public Task<IActionResult> Criar([FromBody] CriarOpcaoRequest? req, CancellationToken ct) => Run(true, async (loja, usuario, _) =>
        await service.Criar(loja, usuario, req ?? throw Vazio(), ct));

    [HttpPatch("{id:guid}")]
    public Task<IActionResult> Atualizar(Guid id, [FromBody] AtualizarOpcaoRequest? req, CancellationToken ct) => Run(true, async (loja, _, _) =>
        await service.Atualizar(loja, id, req ?? throw Vazio(), ct));

    private static GestaoException Vazio() => new("CAMPO_INVALIDO", "Envie os dados.", 422);

    private async Task<IActionResult> Run(bool soGerente, Func<Guid, int, GestaoPapeis.Acesso, Task<object?>> acao)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetEstabelecimentoId() is not Guid loja || HttpContext.GetUserId() is not int usuario) return Unauthorized();
        var acesso = GestaoPapeis.Resolver(HttpContext.TemPermissao, HttpContext.IsSuperAdmin());
        try
        {
            if (acesso.Papel is null || (soGerente && acesso.Papel != GestaoPapeis.Gerente))
                throw new GestaoException("SEM_PERMISSAO", soGerente ? "Só o gerente." : "Você não tem acesso ao caixa.", 403);
            return Ok(ApiResponse<object?>.Ok(await acao(loja, usuario, acesso)));
        }
        catch (GestaoException ex) { return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo)); }
    }
}
