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
            await Task.CompletedTask;
            return StatusCode(410, ApiResponse<object>.Fail(
                "Endpoint legado desativado. Inicie uma sessao operacional em /api/v2/motoboys/me/session/start.",
                "LEGACY_TRACKING_ENDPOINT_DISABLED"));
        }

        [HttpPost("location")]
        public async Task<IActionResult> SendLocation([FromBody] MotoboyLocationRequest request)
        {
            await Task.CompletedTask;
            return StatusCode(410, ApiResponse<object>.Fail(
                "Endpoint legado desativado. Use /api/v2/motoboys/me/session/location.",
                "LEGACY_TRACKING_ENDPOINT_DISABLED"));
        }

        [HttpPost("location/batch")]
        public async Task<IActionResult> SendLocationBatch([FromBody] MotoboyLocationBatchRequest request)
        {
            await Task.CompletedTask;
            return StatusCode(410, ApiResponse<object>.Fail(
                "Endpoint legado desativado. Envie amostras sequenciadas pelo contrato /api/v2.",
                "LEGACY_TRACKING_ENDPOINT_DISABLED"));
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
