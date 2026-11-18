using APIBack.Mesas.Nucleo;
using APIBack.Mesas.Pedidos;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers.Mesas;

/// <summary>
/// Pedidos e itens da mesa (CONTRATO-API §3.5): lançar, aprovar, entregar, mexer no item, cortesia, perda e avisos ao gestor.
/// Quem pode cada comando (letras do contrato) é conferido no serviço; a autonomia do garçom também.
/// </summary>
[Route("api/v2/mesas/pedidos")]
public sealed class MesasPedidosController(IMesasNucleoService nucleo, PedidosService pedidos, CozinhaService leitura) : MesasControllerBase(nucleo)
{
    private CancellationToken Ct => HttpContext.RequestAborted;
    private Task<IActionResult> Ir<T>(Func<MesasContexto, Task<T>> f) where T : notnull => Run(MesasPermissoes.Ver, async ctx => await f(ctx));

    private static ItensRequest Vazio => new(null, null, null, null, null, null);

    // Leituras
    [HttpGet("")]
    public Task<IActionResult> Lista([FromQuery] Guid? mesaId, [FromQuery] string? estado) => Ir(ctx => leitura.PedidosAsync(ctx, mesaId, estado, Ct));

    [HttpGet("garcom")]
    public Task<IActionResult> Garcom([FromQuery] Guid? garcomId) => Ir(ctx => leitura.GarcomAsync(ctx, garcomId, Ct));

    [HttpGet("avisos")]
    public Task<IActionResult> Avisos([FromQuery] bool? lidos) => Ir(ctx => leitura.AvisosAsync(ctx, lidos, Ct));

    // Comandos
    [HttpPost("lancar-pedido")]
    public Task<IActionResult> Lancar([FromBody] LancarPedidoRequest? req) => Ir(ctx => pedidos.LancarAsync(ctx, req ?? new(null, null, null, null, null, null, null), Ct));

    [HttpPost("repetir-rodada")]
    public Task<IActionResult> RepetirRodada([FromBody] RepetirRodadaRequest? req) => Ir(ctx => pedidos.RepetirRodadaAsync(ctx, req ?? new(null, null, null, null), Ct));

    [HttpPost("enviar-producao")]
    public Task<IActionResult> EnviarProducao([FromBody] ItensRequest? req) => Ir(ctx => pedidos.EnviarProducaoAsync(ctx, req ?? Vazio, Ct));

    [HttpPost("aprovar-pedido")]
    public Task<IActionResult> Aprovar([FromBody] ItensRequest? req) => Ir(ctx => pedidos.AprovarAsync(ctx, req ?? Vazio, Ct));

    [HttpPost("recusar-pedido")]
    public Task<IActionResult> Recusar([FromBody] ItensRequest? req) => Ir(ctx => pedidos.RecusarAsync(ctx, req ?? Vazio, Ct));

    [HttpPost("escalar-aprovacao")]
    public Task<IActionResult> Escalar([FromBody] PedidoRequest? req) => Ir(ctx => pedidos.EscalarAsync(ctx, req ?? new(null, null, null, null, null), Ct));

    [HttpPost("reconhecer-pedido")]
    public Task<IActionResult> Reconhecer([FromBody] ReconhecerRequest? req) => Ir(ctx => pedidos.ReconhecerAsync(ctx, req ?? new(null, null, null), Ct));

    [HttpPost("editar-item")]
    public Task<IActionResult> Editar([FromBody] EditarItemRequest? req) => Ir(ctx => pedidos.EditarItemAsync(ctx, req ?? new(null, null, null, null, null, null), Ct));

    [HttpPost("mover-item")]
    public Task<IActionResult> Mover([FromBody] MoverItemRequest? req) => Ir(ctx => pedidos.MoverItemAsync(ctx, req ?? new(null, null, null, null), Ct));

    [HttpPost("mover-item-mesa")]
    public Task<IActionResult> MoverMesa([FromBody] MoverItemMesaRequest? req) =>
        Ir(ctx => pedidos.MoverItemMesaAsync(ctx, req ?? new(null, null, null, null, null, null, null), Ct));

    [HttpPost("cancelar-item")]
    public Task<IActionResult> Cancelar([FromBody] ItemRequest? req) => Ir(ctx => pedidos.CancelarItemAsync(ctx, req ?? new(null, null, null), Ct));

    [HttpPost("prato-voltou")]
    public Task<IActionResult> PratoVoltou([FromBody] ItensRequest? req) => Ir(ctx => pedidos.PratoVoltouAsync(ctx, req ?? Vazio, Ct));

    [HttpPost("pegar-prato")]
    public Task<IActionResult> Pegar([FromBody] ItensRequest? req) => Ir(ctx => pedidos.PegarPratoAsync(ctx, req ?? Vazio, Ct));

    [HttpPost("passar-entrega")]
    public Task<IActionResult> PassarEntrega([FromBody] PassarEntregaRequest? req) => Ir(ctx => pedidos.PassarEntregaAsync(ctx, req ?? new(null, null, null, null), Ct));

    [HttpPost("entregar")]
    public Task<IActionResult> Entregar([FromBody] ItensRequest? req) => Ir(ctx => pedidos.EntregarAsync(ctx, req ?? Vazio, Ct));

    [HttpPost("marcar-retirado")]
    public Task<IActionResult> Retirado([FromBody] ItemRequest? req) => Ir(ctx => pedidos.MarcarRetiradoAsync(ctx, req ?? new(null, null, null), Ct));

    [HttpPost("trocar-quem-leva")]
    public Task<IActionResult> QuemLeva([FromBody] TrocarQuemLevaRequest? req) => Ir(ctx => pedidos.TrocarQuemLevaAsync(ctx, req ?? new(null, null, null), Ct));

    [HttpPost("cobrar-cozinha")]
    public Task<IActionResult> Cobrar([FromBody] PedidoRequest? req) => Ir(ctx => pedidos.CobrarCozinhaAsync(ctx, req ?? new(null, null, null, null, null), Ct));

    [HttpPost("recado-cozinha")]
    public Task<IActionResult> Recado([FromBody] PedidoRequest? req) => Ir(ctx => pedidos.RecadoCozinhaAsync(ctx, req ?? new(null, null, null, null, null), Ct));

    [HttpPost("soltar-etapa")]
    public Task<IActionResult> Soltar([FromBody] PedidoRequest? req) => Ir(ctx => pedidos.SoltarEtapaAsync(ctx, req ?? new(null, null, null, null, null), Ct));

    [HttpPost("cortesia")]
    public Task<IActionResult> Cortesia([FromBody] CortesiaRequest? req) => Ir(ctx => pedidos.CortesiaAsync(ctx, req ?? new(null, null, null, null), Ct));

    [HttpPost("registrar-perda")]
    public Task<IActionResult> Perda([FromBody] PerdaRequest? req) => Ir(ctx => pedidos.RegistrarPerdaAsync(ctx, req ?? new(null, null, null, null, null, null), Ct));

    [HttpPost("marcar-aviso-lido")]
    public Task<IActionResult> AvisoLido([FromBody] AvisoRequest? req) => Ir(ctx => pedidos.MarcarAvisoLidoAsync(ctx, req ?? new(null, null), Ct));
}

/// <summary>Cozinha e bar (CONTRATO-API §3.5 e §4.1): cartões por estação, total para fazer, para levar e os passos do preparo.</summary>
[Route("api/v2/mesas/cozinha")]
public sealed class MesasCozinhaController(IMesasNucleoService nucleo, PedidosService pedidos, CozinhaService leitura) : MesasControllerBase(nucleo)
{
    private CancellationToken Ct => HttpContext.RequestAborted;
    private Task<IActionResult> Ir<T>(Func<MesasContexto, Task<T>> f) where T : notnull => Run(MesasPermissoes.Ver, async ctx => await f(ctx));

    [HttpGet("pedidos")]
    public Task<IActionResult> Pedidos([FromQuery] string? estacao) => Ir(ctx => leitura.CozinhaAsync(ctx, estacao, Ct));

    [HttpGet("total")]
    public Task<IActionResult> Total([FromQuery] string? estacao) => Ir(ctx => leitura.TotalAsync(ctx, estacao, Ct));

    [HttpGet("para-levar")]
    public Task<IActionResult> ParaLevar([FromQuery] Guid? garcomId) => Ir(ctx => leitura.ParaLevarAsync(ctx, garcomId, Ct));

    [HttpPost("avancar-etapa")]
    public Task<IActionResult> Avancar([FromBody] ItemRequest? req) => Ir(ctx => pedidos.AvancarEtapaAsync(ctx, req ?? new(null, null, null), Ct));

    [HttpPost("marcar-item-pronto")]
    public Task<IActionResult> ItemPronto([FromBody] ItemRequest? req) => Ir(ctx => pedidos.MarcarItemProntoAsync(ctx, req ?? new(null, null, null), Ct));

    [HttpPost("marcar-pronto")]
    public Task<IActionResult> Pronto([FromBody] ItemRequest? req) => Ir(ctx => pedidos.MarcarProntoAsync(ctx, req ?? new(null, null, null), Ct));

    [HttpPost("desfazer-passo")]
    public Task<IActionResult> Desfazer([FromBody] ItemRequest? req) => Ir(ctx => pedidos.DesfazerPassoAsync(ctx, req ?? new(null, null, null), Ct));

    [HttpPost("atribuir-preparo")]
    public Task<IActionResult> Atribuir([FromBody] AtribuirPreparoRequest? req) => Ir(ctx => pedidos.AtribuirPreparoAsync(ctx, req ?? new(null, null, null), Ct));

    [HttpPost("trocar-estacao")]
    public Task<IActionResult> TrocarEstacao([FromBody] TrocarEstacaoRequest? req) => Ir(ctx => pedidos.TrocarEstacaoAsync(ctx, req ?? new(null, null, null), Ct));

    [HttpPost("avisar-pronto")]
    public Task<IActionResult> AvisarPronto([FromBody] ItensRequest? req) => Ir(ctx => pedidos.AvisarProntoAsync(ctx, req ?? new(null, null, null, null, null, null), Ct));

    [HttpPost("avisar-atraso")]
    public Task<IActionResult> Atraso([FromBody] PedidoRequest? req) => Ir(ctx => pedidos.AvisarAtrasoAsync(ctx, req ?? new(null, null, null, null, null), Ct));
}
