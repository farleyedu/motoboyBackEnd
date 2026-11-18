using APIBack.DTOs.Common;
using APIBack.Equipe.Ponto;
using APIBack.Extensions;
using APIBack.Mesas.Nucleo;
using APIBack.Security;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

/// <summary>
/// Ponto da equipe (P02, CONTRATO-API §3.2). Cada pessoa bate o próprio ponto (cadastro da Equipe ligado ao usuário); o gerente
/// (dono, gerente do painel, gerente da Equipe ou gerente de salão) também mexe no ponto de outra pessoa com <c>pessoaId</c>.
/// Fora do módulo Mesas: vale para garçom, cozinha, caixa e motoboy.
/// </summary>
[ApiController]
[Route("api/v2/equipe/ponto")]
public sealed class EquipePontoController(IPontoService service) : ControllerBase
{
    [HttpPost("entrar")]
    public Task<IActionResult> Entrar([FromBody] PontoEntrarRequest? req, CancellationToken ct) =>
        Run((ator) => service.Entrar(ator, req ?? new PontoEntrarRequest(), ct));

    [HttpPost("pausar")]
    public Task<IActionResult> Pausar([FromBody] PontoPausarRequest? req, CancellationToken ct) =>
        Run((ator) => service.Pausar(ator, req ?? new PontoPausarRequest(), ct));

    [HttpPost("voltar")]
    public Task<IActionResult> Voltar([FromBody] PontoVoltarRequest? req, CancellationToken ct) =>
        Run((ator) => service.Voltar(ator, req ?? new PontoVoltarRequest(), ct));

    [HttpPost("sair")]
    public Task<IActionResult> Sair([FromBody] PontoSairRequest? req, CancellationToken ct) =>
        Run((ator) => service.Sair(ator, req ?? new PontoSairRequest(), ct));

    /// <summary>Ponto do dia (o meu, ou de <c>pessoaId</c> para o gerente). <c>data</c> padrão = hoje no fuso da loja.</summary>
    [HttpGet("hoje")]
    public Task<IActionResult> Hoje([FromQuery] Guid? pessoaId, [FromQuery] DateOnly? data, CancellationToken ct) =>
        Run((ator) => service.Dia(ator, pessoaId, data, ct));

    /// <summary>Quem está com ponto aberto agora (gerente).</summary>
    [HttpGet("loja")]
    public Task<IActionResult> Loja(CancellationToken ct) => Run((ator) => service.Abertos(ator, ct));

    private async Task<IActionResult> Run<T>(Func<PontoAtor, Task<T>> acao)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetEstabelecimentoId() is not Guid loja || HttpContext.GetUserId() is not int usuario)
            return Unauthorized(ApiResponse<object>.Fail("Entre de novo.", "NAO_AUTENTICADO"));
        try
        {
            var superAdmin = HttpContext.IsSuperAdmin();
            var gestor = superAdmin
                || RoleCatalog.Normalize(HttpContext.GetTipoAcessoEmpresa()) == "dono"
                || HttpContext.TemPermissao(MesasPermissoes.Modulo, MesasPermissoes.Configurar)
                || GestaoPapeis.Resolver(HttpContext.TemPermissao, superAdmin).Papel == GestaoPapeis.Gerente;
            var ator = await service.Ator(loja, usuario, gestor, HttpContext.RequestAborted);
            return Ok(ApiResponse<object>.Ok((await acao(ator))!));
        }
        catch (PontoException ex)
        {
            return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo, ex.Detalhe));
        }
    }
}
