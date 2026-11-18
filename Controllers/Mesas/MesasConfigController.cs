using APIBack.Mesas.Nucleo;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers.Mesas;

/// <summary>
/// Configuração do Mesas (CONTRATO-API §3.1 e §5) e funções da equipe do salão.
/// Ler: toda a equipe (com o Mesas desligado, só dono e gerente). Gravar: dono e gerente; ligar/desligar (<c>chave:'ativo'</c>) só o dono.
/// </summary>
[Route("api/v2/mesas")]
public sealed class MesasConfigController(IMesasNucleoService nucleo) : MesasControllerBase(nucleo)
{
    [HttpGet("config")]
    public Task<IActionResult> Config() => Run(MesasPermissoes.Ver, async ctx =>
        await Nucleo.ConfigAsync(ctx.Loja, HttpContext.RequestAborted), gestorPassaDesligado: true);

    /// <summary>Muda uma chave (caminho com ponto da §5, ex.: <c>gorjeta.sugestoes</c>) ou um capítulo inteiro (ex.: <c>reservas</c>).</summary>
    [HttpPut("config")]
    public Task<IActionResult> Configurar([FromBody] MesasConfigurarRequest? req) => Run(MesasPermissoes.Configurar, async ctx =>
        await Nucleo.ConfigurarAsync(ctx.Loja, ctx.Usuario, ctx.Papel, req ?? new MesasConfigurarRequest(null, null, null), HttpContext.RequestAborted),
        gestorPassaDesligado: true);

    [HttpGet("config/historico")]
    public Task<IActionResult> Historico([FromQuery] int limite = 50) => Run(MesasPermissoes.Configurar, async ctx =>
        await Nucleo.HistoricoAsync(ctx.Loja, limite, HttpContext.RequestAborted), gestorPassaDesligado: true);

    /// <summary>Pessoas ativas da Equipe com a função do salão (garcom, gerente_salao, cozinha, bar, caixa).</summary>
    [HttpGet("equipe")]
    public Task<IActionResult> Equipe() => Run(MesasPermissoes.Configurar, async ctx =>
        await Nucleo.EquipeAsync(ctx.Loja, HttpContext.RequestAborted), gestorPassaDesligado: true);

    /// <summary>Escolhe a função da pessoa; <c>funcaoCodigo: null</c> tira a função.</summary>
    [HttpPut("equipe/{pessoaId:guid}/funcao")]
    public Task<IActionResult> DefinirFuncao(Guid pessoaId, [FromBody] MesasDefinirFuncaoRequest? req) => Run(MesasPermissoes.Configurar, async ctx =>
        await Nucleo.DefinirFuncaoAsync(ctx.Loja, ctx.Usuario, pessoaId, req?.FuncaoCodigo, HttpContext.RequestAborted),
        gestorPassaDesligado: true);
}
