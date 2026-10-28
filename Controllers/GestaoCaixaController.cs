using APIBack.Acontecimentos;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.GestaoLogica.Caixa;
using APIBack.Security;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

public sealed record ConferirContagemRequest(DateOnly Dia, ContagemCaixa Contagem);

/// <summary>
/// Caixa do dia (CONTRATOS §9.7), espelhando a interface CaixaFonte do Painel. Gerente e operador de caixa; o operador só vê e
/// registra hoje. Fechamentos do mês e correção do contado são do gerente.
/// </summary>
[ApiController]
[Route("api/gestao/caixa")]
public sealed class GestaoCaixaController(ICaixaService service) : ControllerBase
{
    [HttpGet("dias/{dia}")]
    public Task<IActionResult> Dia(DateOnly dia, CancellationToken ct) => Run(false, async (loja, _, acesso) =>
    {
        await SoHojeParaOperador(loja, acesso, dia, ct);
        return await service.CarregarDia(loja, dia, ct);
    });

    [HttpPost("abrir")]
    public Task<IActionResult> Abrir([FromBody] AbrirCaixaRequest? req, CancellationToken ct) => Run(false, async (loja, usuario, acesso) =>
        await service.Abrir(loja, usuario, acesso, req ?? throw Vazio(), ct));

    [HttpPost("movimentos")]
    public Task<IActionResult> Movimento([FromBody] MovimentoCaixaRequest? req, CancellationToken ct) => Run(false, async (loja, usuario, acesso) =>
        await service.RegistrarMovimento(loja, usuario, acesso, req ?? throw Vazio(), ct));

    [HttpPost("contagem")]
    public Task<IActionResult> Contagem([FromBody] ConferirContagemRequest? req, CancellationToken ct) => Run(false, async (loja, usuario, acesso) =>
    {
        if (!acesso.PodeLancar) throw new GestaoException("SEM_PERMISSAO", "Você não pode contar o caixa.", 403);
        var r = req ?? throw Vazio();
        return await service.ConferirContagem(loja, usuario, r.Dia, r.Contagem, ct);
    });

    [HttpPost("fechar")]
    public Task<IActionResult> Fechar([FromBody] FecharCaixaRequest? req, CancellationToken ct) => Run(false, async (loja, usuario, acesso) =>
        await service.Fechar(loja, usuario, acesso, req ?? throw Vazio(), ct));

    [HttpGet("fechamentos")]
    public Task<IActionResult> Fechamentos([FromQuery] DateOnly mes, CancellationToken ct) => Run(true, async (loja, _, _) =>
        await service.Fechamentos(loja, mes, ct));

    [HttpPost("fechamentos/{dia}/ajustes")]
    public Task<IActionResult> Ajustar(DateOnly dia, [FromBody] AjustarFechamentoRequest? req, CancellationToken ct) => Run(true, async (loja, usuario, acesso) =>
    {
        GestaoPapeis.ExigirCorrecao(acesso);
        return await service.AjustarFechamento(loja, usuario, dia, req ?? throw Vazio(), ct);
    });

    private async Task SoHojeParaOperador(Guid loja, GestaoPapeis.Acesso acesso, DateOnly dia, CancellationToken ct)
    {
        if (acesso.Papel == GestaoPapeis.OperadorCaixa && dia != await service.Hoje(loja, ct))
            throw new GestaoException("SEM_PERMISSAO", "O operador de caixa vê só o caixa de hoje.", 403);
    }

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
