using System;
using System.Threading.Tasks;
using APIBack.DTOs.Common;
using APIBack.DTOs.Tracking;
using APIBack.Extensions;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    [Route("api/motoboys/me")]
    [ApiController]
    public class MotoboyTrackingController : ControllerBase
    {
        private readonly ITrackingService _trackingService;

        public MotoboyTrackingController(ITrackingService trackingService)
        {
            _trackingService = trackingService;
        }

        [HttpPost("status")]
        public async Task<IActionResult> SetStatus([FromBody] MotoboyStatusRequest request)
        {
            if (!TryResolveActor(out var userId, out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var result = await _trackingService.SetStatusAsync(userId, estabelecimentoId, request);
                return Ok(ApiResponse<MotoboyStatusRealtimeDto>.Ok(result));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, ApiResponse<object>.Fail(ex.Message));
            }
        }

        [HttpPost("location")]
        public async Task<IActionResult> SendLocation([FromBody] MotoboyLocationRequest request)
        {
            if (!TryResolveActor(out var userId, out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var result = await _trackingService.ReceiveLocationAsync(userId, estabelecimentoId, request);
                return Ok(ApiResponse<MotoboyLocationResult>.Ok(result));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, ApiResponse<object>.Fail(ex.Message));
            }
        }

        [HttpPost("location/batch")]
        public async Task<IActionResult> SendLocationBatch([FromBody] MotoboyLocationBatchRequest request)
        {
            if (!TryResolveActor(out var userId, out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var result = await _trackingService.ReceiveLocationBatchAsync(userId, estabelecimentoId, request);
                return Ok(ApiResponse<MotoboyLocationBatchResult>.Ok(result));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, ApiResponse<object>.Fail(ex.Message));
            }
        }

        private bool TryResolveActor(out int userId, out Guid estabelecimentoId, out IActionResult? error)
        {
            userId = HttpContext.GetUserId() ?? 0;
            estabelecimentoId = HttpContext.GetEstabelecimentoId() ?? Guid.Empty;

            if (userId <= 0)
            {
                error = Unauthorized(ApiResponse<object>.Fail("Usuario nao autenticado."));
                return false;
            }

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
