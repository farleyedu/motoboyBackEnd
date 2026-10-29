using APIBack.Acontecimentos;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.GestaoLogica.Despesas;
using APIBack.Security;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

public sealed record FinalidadeRequest(string? Nome);

/// <summary>
/// Contas fixas (recorrências), lembretes, finalidades e equipamentos da loja (CONTRATOS §9.8). Só o gerente: leitura com
/// <c>Financeiro:visualizar</c>, escrita com <c>Financeiro:criar</c>.
/// </summary>
[ApiController]
[Route("api/gestao/despesas")]
public sealed class GestaoDespesasController(IRecorrenciasService service, IFinalidadesService finalidades, IEquipamentosService equipamentos)
    : ControllerBase
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
    public Task<IActionResult> Finalidades(CancellationToken ct) => Run(false, async (loja, _) => await finalidades.Ativas(loja, ct));

    [HttpPost("finalidades")]
    public Task<IActionResult> CriarFinalidade([FromBody] FinalidadeRequest? req, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await finalidades.Criar(loja, usuario, req?.Nome, ct));

    // ---------------------------------------------------------------- L1: editar, reativar e históricos (auditoria-edicao 1, 7, 19)

    [HttpPost("contas-fixas/{id:guid}/reativar")]
    public Task<IActionResult> Reativar(Guid id, [FromBody] MotivoRequest? req, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await service.Reativar(loja, usuario, id, req?.Motivo, ct));

    [HttpGet("contas-fixas/{id:guid}/historico")]
    public Task<IActionResult> HistoricoContaFixa(Guid id, CancellationToken ct) => Run(false, async (loja, _) => await service.Historico(loja, id, ct));

    /// <summary>Finalidades com id e todos os nomes que já tiveram (a tela troca o texto antigo do livro pelo nome atual).</summary>
    [HttpGet("finalidades/cadastro")]
    public Task<IActionResult> CadastroFinalidades(CancellationToken ct) => Run(false, async (loja, _) => await finalidades.Listar(loja, ct));

    [HttpPut("finalidades/{id:guid}")]
    public Task<IActionResult> RenomearFinalidade(Guid id, [FromBody] RenomearFinalidadeRequest? req, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await finalidades.Renomear(loja, usuario, id, req ?? throw Vazio(), ct));

    [HttpPost("finalidades/{id:guid}/desativar")]
    public Task<IActionResult> DesativarFinalidade(Guid id, [FromBody] MotivoRequest? req, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await finalidades.Desativar(loja, usuario, id, req?.Motivo, ct));

    [HttpPost("finalidades/{id:guid}/reativar")]
    public Task<IActionResult> ReativarFinalidade(Guid id, [FromBody] MotivoRequest? req, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await finalidades.Reativar(loja, usuario, id, req?.Motivo, ct));

    [HttpPost("finalidades/{id:guid}/juntar")]
    public Task<IActionResult> JuntarFinalidade(Guid id, [FromBody] JuntarFinalidadeRequest? req, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await finalidades.Juntar(loja, usuario, id, req ?? throw Vazio(), ct));

    [HttpGet("finalidades/{id:guid}/historico")]
    public Task<IActionResult> HistoricoFinalidade(Guid id, CancellationToken ct) => Run(false, async (loja, _) => await finalidades.Historico(loja, id, ct));

    [HttpGet("equipamentos")]
    public Task<IActionResult> Equipamentos([FromQuery] bool todos, CancellationToken ct) => Run(false, async (loja, _) => await equipamentos.Listar(loja, todos, ct));

    [HttpPost("equipamentos")]
    public Task<IActionResult> CriarEquipamento([FromBody] EquipamentoRequest? req, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await equipamentos.Criar(loja, usuario, req ?? throw Vazio(), ct));

    /// <summary>Uma vez por aparelho: o Painel manda o que ainda estava no navegador. Repetir não duplica.</summary>
    [HttpPost("equipamentos/importar")]
    public Task<IActionResult> ImportarEquipamentos([FromBody] ImportarEquipamentosRequest? req, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await equipamentos.Importar(loja, usuario, req ?? throw Vazio(), ct));

    [HttpPut("equipamentos/{id}")]
    public Task<IActionResult> AtualizarEquipamento(string id, [FromBody] EquipamentoRequest? req, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await equipamentos.Atualizar(loja, usuario, id, req ?? throw Vazio(), ct));

    [HttpPost("equipamentos/{id}/arquivar")]
    public Task<IActionResult> ArquivarEquipamento(string id, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await equipamentos.Arquivar(loja, usuario, id, true, ct));

    [HttpPost("equipamentos/{id}/reativar")]
    public Task<IActionResult> ReativarEquipamento(string id, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await equipamentos.Arquivar(loja, usuario, id, false, ct));

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
