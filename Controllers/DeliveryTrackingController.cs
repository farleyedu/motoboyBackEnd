using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Tracking;
using APIBack.Extensions;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    [Route("api/delivery")]
    [ApiController]
    public class DeliveryTrackingController : ControllerBase
    {
        private readonly ITrackingService _trackingService;

        public DeliveryTrackingController(ITrackingService trackingService)
        {
            _trackingService = trackingService;
        }

        [HttpGet("map-state")]
        [RequirePermission("Delivery", "visualizar")]
        public async Task<IActionResult> GetMapState()
        {
            var estabelecimentoId = HttpContext.GetEstabelecimentoId();
            if (!estabelecimentoId.HasValue || estabelecimentoId.Value == Guid.Empty)
            {
                return Unauthorized(ApiResponse<object>.Fail("Sessao sem estabelecimento ativo."));
            }

            var result = await _trackingService.GetMapStateAsync(estabelecimentoId.Value);
            return Ok(ApiResponse<DeliveryMapStateDto>.Ok(result));
        }

        [HttpGet("motoboys/{motoboyId:int}/location-history")]
        [RequirePermission("Delivery", "visualizar")]
        public async Task<IActionResult> GetLocationHistory([FromRoute] int motoboyId, [FromQuery] string? date)
        {
            var estabelecimentoId = HttpContext.GetEstabelecimentoId();
            if (!estabelecimentoId.HasValue || estabelecimentoId.Value == Guid.Empty)
            {
                return Unauthorized(ApiResponse<object>.Fail("Sessao sem estabelecimento ativo."));
            }

            var localDate = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3));
            if (!string.IsNullOrWhiteSpace(date) && !DateOnly.TryParse(date, out localDate))
            {
                return BadRequest(ApiResponse<object>.Fail("Parametro date invalido. Use YYYY-MM-DD."));
            }

            var result = await _trackingService.GetLocationHistoryAsync(estabelecimentoId.Value, motoboyId, localDate);
            return Ok(ApiResponse<object>.Ok(new
            {
                motoboyId,
                localDate,
                points = result
            }));
        }
    }
}
