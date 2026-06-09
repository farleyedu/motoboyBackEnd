using System;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Tracking;
using APIBack.Extensions;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    [Route("api/delivery/simulator")]
    [ApiController]
    [RequirePermission("Delivery", "visualizar")]
    public class DeliverySimulatorController : ControllerBase
    {
        private readonly ITrackingService _trackingService;

        public DeliverySimulatorController(ITrackingService trackingService)
        {
            _trackingService = trackingService;
        }

        [HttpGet("motoboys")]
        public async Task<IActionResult> GetMotoboys()
        {
            if (!TryGetEstabelecimentoId(out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var state = await _trackingService.GetMapStateAsync(estabelecimentoId);
            return Ok(ApiResponse<object>.Ok(new
            {
                motoboys = state.Motoboys.OrderBy(m => m.Nome).ToList()
            }));
        }

        [HttpPost("motoboys")]
        public async Task<IActionResult> CreateMotoboy([FromBody] CreateSimulatorMotoboyRequest request)
        {
            if (!TryGetEstabelecimentoId(out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var motoboy = await _trackingService.CreateSimulatorMotoboyAsync(estabelecimentoId, request);
            return Ok(ApiResponse<MotoboyMapDto>.Ok(motoboy));
        }

        [HttpPost("motoboys/{motoboyId:int}/status")]
        public async Task<IActionResult> SetStatus([FromRoute] int motoboyId, [FromBody] MotoboyStatusRequest request)
        {
            if (!TryGetEstabelecimentoId(out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var result = await _trackingService.SetSimulatorStatusAsync(estabelecimentoId, motoboyId, request);
                return Ok(ApiResponse<MotoboyStatusRealtimeDto>.Ok(result));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, ApiResponse<object>.Fail(ex.Message));
            }
        }

        [HttpPost("motoboys/{motoboyId:int}/session")]
        public async Task<IActionResult> StartSession([FromRoute] int motoboyId)
        {
            if (!TryGetEstabelecimentoId(out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var result = await _trackingService.StartSimulatorSessionAsync(estabelecimentoId, motoboyId);
                return Ok(ApiResponse<SimulatorMotoboySessionResponse>.Ok(result));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, ApiResponse<object>.Fail(ex.Message));
            }
        }

        [HttpPost("motoboys/{motoboyId:int}/location")]
        public async Task<IActionResult> SendLocation([FromRoute] int motoboyId, [FromBody] MotoboyLocationRequest request)
        {
            if (!TryGetEstabelecimentoId(out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var result = await _trackingService.ReceiveSimulatorLocationAsync(estabelecimentoId, motoboyId, request);
                return Ok(ApiResponse<MotoboyLocationResult>.Ok(result));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, ApiResponse<object>.Fail(ex.Message));
            }
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
