using System;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Delivery;
using APIBack.DTOs.Tracking;
using APIBack.Extensions;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    [Route("api/delivery/simulator")]
    [ApiController]
    [RequirePermission("Delivery", "gestao_motoboy")]
    public class DeliverySimulatorController : ControllerBase
    {
        private readonly IOperationalSessionService _operationalSessionService;
        private readonly IPedidoQueueService _queueService;

        public DeliverySimulatorController(IOperationalSessionService operationalSessionService, IPedidoQueueService queueService)
        {
            _operationalSessionService = operationalSessionService;
            _queueService = queueService;
        }

        /// <summary>
        /// Altera qualquer dado do pedido para testes (endereco, itens, pagamento, prazo e
        /// ate "feito ha N minutos"). Motoboy, fila e status continuam nos comandos de fila.
        /// </summary>
        [HttpPut("pedidos/{pedidoId:int}")]
        public async Task<IActionResult> UpdatePedido([FromRoute] int pedidoId, [FromBody] SimulatorPedidoRequest request)
        {
            if (!TryGetEstabelecimentoId(out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var result = await _queueService.UpdatePedidoForSimulatorAsync(
                    estabelecimentoId, HttpContext.GetUserId() ?? 0, pedidoId, request);
                return Ok(ApiResponse<CreatedPedidoDto>.Ok(result));
            }
            catch (APIBack.Service.DeliveryDomainException ex)
            {
                return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details));
            }
        }

        [HttpGet("motoboys")]
        public async Task<IActionResult> GetMotoboys()
        {
            if (!TryGetEstabelecimentoId(out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var candidates = await _operationalSessionService.GetSimulatorCandidatesAsync(estabelecimentoId);
                return Ok(ApiResponse<object>.Ok(new
                {
                    motoboys = candidates.Select(candidate => new MotoboyMapDto
                    {
                        Id = candidate.MotoboyId,
                        Nome = candidate.Nome,
                        Avatar = candidate.Avatar,
                        Status = candidate.Eligible ? "offline" : "online"
                    }).ToList()
                }));
            }
            catch (APIBack.Service.DeliveryDomainException ex)
            {
                return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details));
            }
        }

        [HttpPost("motoboys")]
        public async Task<IActionResult> CreateMotoboy([FromBody] CreateSimulatorMotoboyRequest request)
        {
            if (!TryGetEstabelecimentoId(out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var motoboy = await _operationalSessionService.CreateSimulatorMotoboyAsync(
                    HttpContext.GetUserId() ?? 0, HttpContext.IsSuperAdmin(), estabelecimentoId, request);
                return Ok(ApiResponse<MotoboyMapDto>.Ok(motoboy));
            }
            catch (APIBack.Service.DeliveryDomainException ex)
            {
                return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details));
            }
        }

        [HttpPost("motoboys/{motoboyId:int}/status")]
        public async Task<IActionResult> SetStatus([FromRoute] int motoboyId, [FromBody] MotoboyStatusRequest request)
        {
            if (!TryGetEstabelecimentoId(out var estabelecimentoId, out var error))
            {
                return error!;
            }

            await Task.CompletedTask;
            return StatusCode(410, ApiResponse<object>.Fail(
                "Status do simulador agora deriva da sessao operacional.",
                "LEGACY_SIMULATOR_ENDPOINT_DISABLED"));
        }

        [HttpPost("motoboys/{motoboyId:int}/session")]
        public async Task<IActionResult> StartSession([FromRoute] int motoboyId)
        {
            if (!TryGetEstabelecimentoId(out var estabelecimentoId, out var error))
            {
                return error!;
            }

            await Task.CompletedTask;
            return StatusCode(410, ApiResponse<object>.Fail(
                "Selecao por motoboyId foi desativada. Use /api/v2/delivery/simulator/sessions/auto-start.",
                "LEGACY_SIMULATOR_ENDPOINT_DISABLED"));
        }

        [HttpPost("motoboys/{motoboyId:int}/location")]
        public async Task<IActionResult> SendLocation([FromRoute] int motoboyId, [FromBody] MotoboyLocationRequest request)
        {
            if (!TryGetEstabelecimentoId(out var estabelecimentoId, out var error))
            {
                return error!;
            }

            await Task.CompletedTask;
            return StatusCode(410, ApiResponse<object>.Fail(
                "GPS direto do simulador foi desativado. Use o token operacional no endpoint /api/v2.",
                "LEGACY_SIMULATOR_ENDPOINT_DISABLED"));
        }

        private bool TryGetEstabelecimentoId(out Guid estabelecimentoId, out IActionResult? error)
        {
            estabelecimentoId = HttpContext.GetEstabelecimentoId() ?? Guid.Empty;
            if (estabelecimentoId == Guid.Empty)
            {
                error = Unauthorized(ApiResponse<object>.Fail("Sessao sem estabelecimento ativo."));
                return false;
            }

            error = null;
            return true;
        }
    }
}
