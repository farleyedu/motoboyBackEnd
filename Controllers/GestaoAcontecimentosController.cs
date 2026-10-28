using APIBack.Acontecimentos;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Security;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

/// <summary>
/// Livro de acontecimentos da loja (docs/zippy-gestao/CONTRATOS.md). Modulos do backend usam IAcontecimentosService
/// direto; estas rotas servem o Painel. O papel (GestaoPapeis) decide o que cada pessoa le e escreve.
/// </summary>
[ApiController]
[Route("api/gestao")]
public sealed class GestaoAcontecimentosController(IAcontecimentosService service, TimeProvider relogio) : ControllerBase
{
    // Fuso da loja da requisicao atual (preenchido em Run; o controller vive uma requisicao).
    private TimeZoneInfo fuso = HorarioRegras.ResolverFuso(null);

    [HttpGet("acesso")]
    public Task<IActionResult> Acesso() => Run((_, _, acesso, _) => Task.FromResult<object>(acesso));

    [HttpGet("acontecimentos")]
    public Task<IActionResult> LinhaDoTempo([FromQuery] PeriodoFiltro filtro, CancellationToken ct) =>
        Run(async (loja, _, acesso, hoje) => await service.LinhaDoTempo(loja, GestaoPapeis.RestringirLeitura(acesso, filtro, hoje), ct));

    [HttpGet("acontecimentos/{id:guid}")]
    public Task<IActionResult> Obter(Guid id, CancellationToken ct) => Run(async (loja, _, acesso, hoje) =>
    {
        var dto = await service.Obter(loja, id, ct) ?? throw new GestaoException("NAO_ENCONTRADO", "Acontecimento não encontrado.", 404);
        if (acesso.Papel != GestaoPapeis.Gerente)
            GestaoPapeis.RestringirLeitura(acesso, new PeriodoFiltro { Dia = AcontecimentoRegras.DiaLocal(dto.DataFato, fuso), Modulo = dto.Modulo }, hoje);
        return dto;
    });

    [HttpPost("acontecimentos")]
    public Task<IActionResult> Publicar([FromBody] PublicarAcontecimentoRequest request, CancellationToken ct) =>
        Run(async (loja, usuario, acesso, hoje) =>
        {
            if (request?.DataFato is { } fato) GestaoPapeis.ValidarPublicacao(acesso, request, AcontecimentoRegras.DiaLocal(fato, fuso), hoje);
            else GestaoPapeis.ValidarPublicacao(acesso, request ?? new(), hoje, hoje);
            return await service.Publicar(loja, usuario, request!, ct);
        });

    [HttpPost("acontecimentos/{id:guid}/estorno")]
    public Task<IActionResult> Estornar(Guid id, [FromBody] EstornoRequest request, CancellationToken ct) =>
        Run(async (loja, usuario, acesso, _) =>
        {
            GestaoPapeis.ExigirCorrecao(acesso);
            return await service.Estornar(loja, usuario, id, request, ct);
        });

    [HttpPost("ajustes")]
    public Task<IActionResult> Ajustar([FromBody] AjusteRequest request, CancellationToken ct) =>
        Run(async (loja, usuario, acesso, _) =>
        {
            GestaoPapeis.ExigirCorrecao(acesso);
            return await service.Ajustar(loja, usuario, request, ct);
        });

    [HttpPost("liquidacoes")]
    public Task<IActionResult> Liquidar([FromBody] LiquidarRequest request, CancellationToken ct) =>
        Run(async (loja, usuario, acesso, _) =>
        {
            if (!acesso.PodeLancar || acesso.Papel != GestaoPapeis.Gerente)
                throw new GestaoException("SEM_PERMISSAO", "Só o gerente registra pagamentos.", 403);
            return await service.Liquidar(loja, usuario, request, ct);
        });

    [HttpPost("contas/{cartaoId:guid}/faturas/{faturaMes}/pagamento")]
    public Task<IActionResult> PagarFatura(Guid cartaoId, DateOnly faturaMes, [FromBody] LiquidarRequest request, CancellationToken ct) =>
        Run(async (loja, usuario, acesso, _) =>
        {
            if (!acesso.PodeLancar || acesso.Papel != GestaoPapeis.Gerente)
                throw new GestaoException("SEM_PERMISSAO", "Só o gerente paga a fatura.", 403);
            return await service.PagarFatura(loja, usuario, cartaoId, AcontecimentoRegras.Mes(faturaMes), request, ct);
        });

    [HttpGet("lancamentos")]
    public Task<IActionResult> Lancamentos([FromQuery] PeriodoFiltro filtro, CancellationToken ct) =>
        Run(async (loja, _, acesso, hoje) => await service.Lancamentos(loja, GestaoPapeis.RestringirLeitura(acesso, filtro, hoje), ct));

    [HttpGet("movimentos-estoque")]
    public Task<IActionResult> Movimentos([FromQuery] PeriodoFiltro filtro, CancellationToken ct) =>
        Run(async (loja, _, acesso, hoje) =>
        {
            if (acesso.Papel != GestaoPapeis.Gerente) filtro.Modulo ??= ModuloGestao.Estoque;
            return await service.Movimentos(loja, GestaoPapeis.RestringirLeitura(acesso, filtro, hoje), ct);
        });

    [HttpGet("contas")]
    public Task<IActionResult> Contas(CancellationToken ct) => Run(async (loja, _, acesso, _) =>
    {
        var contas = await service.Contas(loja, ct);
        // Contagem as cegas: o operador ve so a conta Caixa, sem saldo.
        return acesso.Papel == GestaoPapeis.Gerente ? contas
            : contas.Where(k => k.Tipo == TipoConta.Caixa && k.Ativo).Select(k => k with { Saldo = 0 }).ToList();
    });

    [HttpPost("contas")]
    public Task<IActionResult> CriarConta([FromBody] ContaRequest request, CancellationToken ct) =>
        Run(async (loja, usuario, acesso, _) =>
        {
            if (acesso.Papel != GestaoPapeis.Gerente || !HttpContext.IsSuperAdmin() && !HttpContext.TemPermissao("Financeiro", "configurar"))
                throw new GestaoException("SEM_PERMISSAO", "Só quem configura o Financeiro cria contas.", 403);
            return await service.CriarConta(loja, usuario, request, ct);
        });

    [HttpGet("meses-fechados")]
    public Task<IActionResult> MesesFechados(CancellationToken ct) => Run(async (loja, _, acesso, _) =>
        acesso.Papel == GestaoPapeis.Gerente ? await service.MesesFechados(loja, ct)
            : throw new GestaoException("SEM_PERMISSAO", "Só o gerente vê o histórico de meses.", 403));

    [HttpPost("meses/{mes}/fechar")]
    public Task<IActionResult> FecharMes(DateOnly mes, [FromBody] FecharMesRequest request, CancellationToken ct) =>
        Run(async (loja, usuario, acesso, _) =>
        {
            GestaoPapeis.ExigirFecharMes(acesso);
            return await service.FecharMes(loja, usuario, AcontecimentoRegras.Mes(mes), request, ct);
        });

    [HttpGet("papeis/{papel}/permissoes")]
    public Task<IActionResult> PermissoesPadrao(string papel) => Run((_, _, acesso, _) =>
        acesso.Papel == GestaoPapeis.Gerente ? Task.FromResult<object>(GestaoPapeis.PermissoesPadrao(papel))
            : throw new GestaoException("SEM_PERMISSAO", "Só o gerente vê os papéis.", 403));

    private async Task<IActionResult> Run(Func<Guid, int, GestaoPapeis.Acesso, DateOnly, Task<object>> acao)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetEstabelecimentoId() is not Guid loja || HttpContext.GetUserId() is not int usuario) return Unauthorized();
        var acesso = GestaoPapeis.Resolver(HttpContext.TemPermissao, HttpContext.IsSuperAdmin());
        try
        {
            fuso = await service.Fuso(loja, HttpContext.RequestAborted);
            var hoje = AcontecimentoRegras.DiaLocal(relogio.GetUtcNow(), fuso);
            return Ok(ApiResponse<object>.Ok(await acao(loja, usuario, acesso, hoje)));
        }
        catch (GestaoException ex) { return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo)); }
    }
}
