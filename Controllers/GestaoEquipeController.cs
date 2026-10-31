using APIBack.Acontecimentos;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.GestaoLogica.Equipe;
using APIBack.Security;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

/// <summary>
/// Equipe e remuneração (CONTRATOS §9.6). Só o gerente: leitura com <c>Financeiro:visualizar</c>; escrita com
/// <c>Financeiro:criar</c>; desconto (correção) com <c>Financeiro:editar</c> ou <c>cancelar</c>.
/// </summary>
[ApiController]
[Route("api/gestao/equipe")]
public sealed class GestaoEquipeController(IEquipeService service) : ControllerBase
{
    [HttpGet("pessoas")]
    public Task<IActionResult> Pessoas([FromQuery] bool todas, CancellationToken ct) => Run(Leitura, async (loja, _) =>
        await service.Pessoas(loja, todas, ct));

    [HttpPost("pessoas")]
    public Task<IActionResult> Criar([FromBody] PessoaRequest? req, CancellationToken ct) => Run(Escrita, async (loja, usuario) =>
        await service.SalvarPessoa(loja, usuario, null, req ?? throw Vazio(), ct));

    [HttpPut("pessoas/{id:guid}")]
    public Task<IActionResult> Atualizar(Guid id, [FromBody] PessoaRequest? req, CancellationToken ct) => Run(Escrita, async (loja, usuario) =>
        await service.SalvarPessoa(loja, usuario, id, req ?? throw Vazio(), ct));

    /// <summary>Versões do cadastro (nome, vínculo e regra), da mais nova para a mais antiga.</summary>
    [HttpGet("pessoas/{id:guid}/historico")]
    public Task<IActionResult> Historico(Guid id, CancellationToken ct) => Run(Leitura, async (loja, _) =>
        await service.HistoricoDaPessoa(loja, id, ct));

    /// <summary>Usuários do painel com acesso à loja, para ligar o colaborador ao Zippy (Integração 6A).</summary>
    [HttpGet("usuarios")]
    public Task<IActionResult> Usuarios(CancellationToken ct) => Run(Leitura, async (loja, _) => await service.UsuariosDaLoja(loja, ct));

    [HttpGet("semana")]
    public Task<IActionResult> Semana([FromQuery] DateOnly inicio, CancellationToken ct) => Run(Leitura, async (loja, _) =>
        await service.Semana(loja, inicio, ct));

    /// <summary>Entregas concluídas atribuídas à pessoa no dia do turno (conferência antes de confirmar).</summary>
    [HttpGet("atividade")]
    public Task<IActionResult> Atividade([FromQuery] Guid pessoaId, [FromQuery] DateOnly data, CancellationToken ct) => Run(Leitura, async (loja, _) =>
        await service.AtividadeDoTurno(loja, pessoaId, data, ct));

    [HttpPost("turnos")]
    public Task<IActionResult> Turno([FromBody] MudarTurnoRequest? req, CancellationToken ct) => Run(Escrita, async (loja, usuario) =>
        await service.MudarTurno(loja, usuario, req ?? throw Vazio(), ct));

    [HttpGet("resumo")]
    public Task<IActionResult> Resumo([FromQuery] DateOnly mes, CancellationToken ct) => Run(Leitura, async (loja, _) =>
        await service.Resumo(loja, mes, ct));

    [HttpPost("adiantamentos")]
    public Task<IActionResult> Adiantar([FromBody] AdiantamentoRequest? req, CancellationToken ct) => Run(Escrita, async (loja, usuario) =>
        await service.Adiantar(loja, usuario, req ?? throw Vazio(), ct));

    [HttpPost("descontos")]
    public Task<IActionResult> Descontar([FromBody] DescontoRequest? req, CancellationToken ct) => Run(Correcao, async (loja, usuario) =>
        await service.Descontar(loja, usuario, req ?? throw Vazio(), ct));

    [HttpPost("pagamentos")]
    public Task<IActionResult> Pagar([FromBody] PagamentoRequest? req, CancellationToken ct) => Run(Escrita, async (loja, usuario) =>
        await service.Pagar(loja, usuario, req ?? throw Vazio(), ct));

    private static bool Leitura(GestaoPapeis.Acesso a) => a.Papel == GestaoPapeis.Gerente;
    private static bool Escrita(GestaoPapeis.Acesso a) => a.Papel == GestaoPapeis.Gerente && a.PodeLancar;
    private static bool Correcao(GestaoPapeis.Acesso a) => a.Papel == GestaoPapeis.Gerente && a.PodeCorrigir;

    private static GestaoException Vazio() => new("CAMPO_INVALIDO", "Envie os dados.", 422);

    private async Task<IActionResult> Run(Func<GestaoPapeis.Acesso, bool> pode, Func<Guid, int, Task<object>> acao)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetEstabelecimentoId() is not Guid loja || HttpContext.GetUserId() is not int usuario) return Unauthorized();
        var acesso = GestaoPapeis.Resolver(HttpContext.TemPermissao, HttpContext.IsSuperAdmin());
        try
        {
            if (!pode(acesso)) throw new GestaoException("SEM_PERMISSAO", "A Equipe é do gerente.", 403);
            return Ok(ApiResponse<object>.Ok(await acao(loja, usuario)));
        }
        catch (GestaoException ex) { return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo)); }
    }
}
