using APIBack.Acontecimentos;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.GestaoLogica.Despesas;
using APIBack.Security;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

public sealed record FinalidadeRequest(string? Nome);

/// <summary>
/// Contas fixas (recorrências), lembretes e finalidades da loja (CONTRATOS §9.8). Só o gerente: leitura com
/// <c>Financeiro:visualizar</c>, escrita com <c>Financeiro:criar</c>.
/// </summary>
[ApiController]
[Route("api/gestao/despesas")]
public sealed class GestaoDespesasController(IRecorrenciasService service) : ControllerBase
{
    [HttpGet("contas-fixas")]
    public Task<IActionResult> Listar([FromQuery] bool todas, CancellationToken ct) => Run(false, async (loja, _) => await service.Listar(loja, todas, ct));

    [HttpPost("contas-fixas")]
    public Task<IActionResult> Criar([FromBody] ContaFixaRequest? req, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await service.Salvar(loja, usuario, null, req ?? throw Vazio(), ct));

    [HttpPut("contas-fixas/{id:guid}")]
    public Task<IActionResult> Atualizar(Guid id, [FromBody] ContaFixaRequest? req, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await service.Salvar(loja, usuario, id, req ?? throw Vazio(), ct));

    [HttpPost("contas-fixas/{id:guid}/arquivar")]
    public Task<IActionResult> Arquivar(Guid id, CancellationToken ct) => Run(true, async (loja, usuario) => await service.Arquivar(loja, usuario, id, ct));

    [HttpGet("contas-fixas/ocorrencias")]
    public Task<IActionResult> Ocorrencias([FromQuery] DateOnly mes, CancellationToken ct) => Run(false, async (loja, _) =>
        await service.Ocorrencias(loja, mes, ct));

    [HttpPost("contas-fixas/gerar")]
    public Task<IActionResult> Gerar([FromQuery] DateOnly mes, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await service.Gerar(loja, usuario, mes, ct));

    [HttpPost("contas-fixas/{id:guid}/lancar")]
    public Task<IActionResult> Lancar(Guid id, [FromBody] LancarOcorrenciaRequest? req, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await service.Lancar(loja, usuario, id, req ?? throw Vazio(), ct));

    [HttpGet("lembretes")]
    public Task<IActionResult> Lembretes([FromQuery] int dias = 3, CancellationToken ct = default) => Run(false, async (loja, _) =>
        await service.Lembretes(loja, dias, ct));

    [HttpGet("finalidades")]
    public Task<IActionResult> Finalidades(CancellationToken ct) => Run(false, async (loja, _) => await service.Finalidades(loja, ct));

    [HttpPost("finalidades")]
    public Task<IActionResult> CriarFinalidade([FromBody] FinalidadeRequest? req, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await service.CriarFinalidade(loja, usuario, req?.Nome, ct));

    private static GestaoException Vazio() => new("CAMPO_INVALIDO", "Envie os dados.", 422);

    private async Task<IActionResult> Run(bool escrita, Func<Guid, int, Task<object>> acao)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetEstabelecimentoId() is not Guid loja || HttpContext.GetUserId() is not int usuario) return Unauthorized();
        var acesso = GestaoPapeis.Resolver(HttpContext.TemPermissao, HttpContext.IsSuperAdmin());
        try
        {
            if (acesso.Papel != GestaoPapeis.Gerente || (escrita && !acesso.PodeLancar))
                throw new GestaoException("SEM_PERMISSAO", "Despesas são do gerente.", 403);
            return Ok(ApiResponse<object>.Ok(await acao(loja, usuario)));
        }
        catch (GestaoException ex) { return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo)); }
    }
}
