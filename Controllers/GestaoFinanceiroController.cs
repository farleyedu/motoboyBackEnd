using APIBack.Acontecimentos;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.GestaoLogica.Compras;
using APIBack.GestaoLogica.Financeiro;
using APIBack.Security;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

/// <summary>
/// Lista de compras do mês e compra à mão (Integração 6B). Mesmo acesso do registro: só o gerente; lançar exige poder lançar.
/// A compra por documento, o detalhe e a conferência seguem em <see cref="GestaoComprasController"/>.
/// </summary>
[ApiController]
[Route("api/gestao/compras")]
public sealed class GestaoComprasListaController(IComprasListaService service) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> Listar([FromQuery] DateOnly? mes, [FromQuery] DateOnly? dia, CancellationToken ct) =>
        GestaoAcesso.Gerente(this, false, async (loja, _) => await service.Listar(loja, mes, dia, ct));

    [HttpPost("manual")]
    public Task<IActionResult> Manual([FromBody] CompraManualRequest? req, CancellationToken ct) =>
        GestaoAcesso.Gerente(this, true, async (loja, usuario) => await service.RegistrarManual(loja, usuario,
            req ?? throw new GestaoException("CAMPO_INVALIDO", "Envie a compra.", 422), ct));
}

/// <summary>Agregados do Financeiro (Integração 6B): contas a pagar sem limite de mês e o resumo do mês.</summary>
[ApiController]
[Route("api/gestao/financeiro")]
public sealed class GestaoFinanceiroController(IFinanceiroService service, IAcontecimentosService livro) : ControllerBase
{
    /// <summary>Despesas a pagar com vencimento até <c>ate</c> (padrão: hoje + 7 dias), de qualquer mês, com as correções.</summary>
    [HttpGet("a-pagar")]
    public Task<IActionResult> APagar([FromQuery] DateOnly? ate, CancellationToken ct) => GestaoAcesso.Gerente(this, false, async (loja, _) =>
        await service.APagar(loja, ate ?? (await Hoje(loja, ct)).AddDays(7), ct));

    [HttpGet("resumo")]
    public Task<IActionResult> Resumo([FromQuery] DateOnly? mes, CancellationToken ct) => GestaoAcesso.Gerente(this, false, async (loja, _) =>
    {
        var hoje = await Hoje(loja, ct);
        return await service.Resumo(loja, mes ?? hoje, hoje, ct);
    });

    private async Task<DateOnly> Hoje(Guid loja, CancellationToken ct) =>
        AcontecimentoRegras.DiaLocal(DateTimeOffset.UtcNow, await livro.Fuso(loja, ct));
}

/// <summary>Acesso comum das rotas da gestão que só o gerente usa.</summary>
internal static class GestaoAcesso
{
    public static async Task<IActionResult> Gerente(ControllerBase ctrl, bool escreve, Func<Guid, int, Task<object>> acao)
    {
        var http = ctrl.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        if (http.GetEstabelecimentoId() is not Guid loja || http.GetUserId() is not int usuario) return ctrl.Unauthorized();
        var acesso = GestaoPapeis.Resolver(http.TemPermissao, http.IsSuperAdmin());
        try
        {
            if (acesso.Papel != GestaoPapeis.Gerente) throw new GestaoException("SEM_PERMISSAO", "Só o gerente vê o Financeiro e as compras.", 403);
            if (escreve && !acesso.PodeLancar) throw new GestaoException("SEM_PERMISSAO", "Você não pode lançar no Financeiro.", 403);
            return ctrl.Ok(ApiResponse<object>.Ok(await acao(loja, usuario)));
        }
        catch (GestaoException ex) { return ctrl.StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo)); }
    }
}
