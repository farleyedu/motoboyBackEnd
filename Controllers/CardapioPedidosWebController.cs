using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Cardapio;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    /// <summary>
    /// Pedidos do cardapio web que o cliente ja confirmou pelo WhatsApp e esperam o restaurante aceitar ou recusar.
    /// Aceitar cria o pedido real no delivery (origem cardapio_web) e so entao avisa o cliente.
    /// </summary>
    [Route("api/v2/delivery/cardapio-pedidos")]
    [ApiController]
    public sealed class CardapioPedidosWebController : ControllerBase
    {
        private readonly ICardapioPedidoWebService _service;

        public CardapioPedidosWebController(ICardapioPedidoWebService service)
        {
            _service = service;
        }

        [HttpGet("aguardando")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> ListarAguardando() =>
            ExecuteAsync((estabelecimentoId, _) => _service.ListarAguardandoAsync(estabelecimentoId));

        [HttpPost("{id:guid}/aceitar")]
        [RequirePermission("Delivery", "criar_pedido")]
        public Task<IActionResult> Aceitar(Guid id) =>
            ExecuteAsync((estabelecimentoId, userId) => _service.AceitarAsync(estabelecimentoId, userId, id));

        [HttpPost("{id:guid}/recusar")]
        [RequirePermission("Delivery", "criar_pedido")]
        public Task<IActionResult> Recusar(Guid id, [FromBody] RecusarCardapioPedidoRequest? request) =>
            ExecuteAsync(async (estabelecimentoId, _) =>
            {
                await _service.RecusarAsync(estabelecimentoId, id, request?.Motivo);
                return new { id, status = CardapioPedidoStatus.Recusado };
            });

        /// <summary>Disparo manual: atendente/cozinha avisa que o pedido de retirada ja esta pronto.</summary>
        [HttpPost("{id:guid}/pronto-retirada")]
        [RequirePermission("Delivery", "criar_pedido")]
        public Task<IActionResult> ProntoParaRetirada(Guid id) =>
            ExecuteAsync(async (estabelecimentoId, _) =>
            {
                await _service.ProntoParaRetiradaAsync(estabelecimentoId, id);
                return new { id };
            });

        private async Task<IActionResult> ExecuteAsync<T>(Func<Guid, int, Task<T>> action)
        {
            var userId = HttpContext.GetUserId() ?? 0;
            var estabelecimentoId = HttpContext.GetEstabelecimentoId() ?? Guid.Empty;
            if (userId <= 0 || estabelecimentoId == Guid.Empty)
            {
                return Unauthorized(ApiResponse<object>.Fail("Contexto autenticado invalido.", "UNAUTHENTICATED"));
            }

            try
            {
                return Ok(ApiResponse<T>.Ok(await action(estabelecimentoId, userId)));
            }
            catch (DeliveryDomainException ex)
            {
                return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details));
            }
        }
    }
}
