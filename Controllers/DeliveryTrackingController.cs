using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Tracking;
using APIBack.Extensions;
using APIBack.Service.Interface;
using APIBack.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Linq;

namespace APIBack.Controllers
{
    [Route("api/delivery")]
    [ApiController]
    public class DeliveryTrackingController : ControllerBase
    {
        private readonly ITrackingService _trackingService;
        private readonly IOperationalSessionService _operationalSessionService;
        private readonly DeliveryTrackingOptions _options;

        public DeliveryTrackingController(
            ITrackingService trackingService,
            IOperationalSessionService operationalSessionService,
            IOptions<DeliveryTrackingOptions> options)
        {
            _trackingService = trackingService;
            _operationalSessionService = operationalSessionService;
            _options = options.Value;
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
            if (_options.Enabled)
            {
                var snapshot = await _operationalSessionService.GetSnapshotAsync(estabelecimentoId.Value);
                result.ServerTimeUtc = snapshot.ServerTimeUtc;
                result.Motoboys = snapshot.Motoboys.Select(m => new MotoboyMapDto
                {
                    Id = m.MotoboyId,
                    Nome = m.Nome,
                    Avatar = m.Avatar,
                    Status = m.Status,
                    Latitude = m.Location?.Latitude,
                    Longitude = m.Location?.Longitude,
                    AccuracyMeters = m.Location?.AccuracyMeters,
                    SpeedMps = m.Location?.SpeedMps,
                    HeadingDegrees = m.Location?.HeadingDegrees,
                    TrackingMode = m.Location?.TrackingMode ?? "online_idle",
                    Quality = m.Location?.Quality ?? "unknown",
                    ClientTimestampUtc = m.Location?.CapturedAtUtc,
                    ServerReceivedAtUtc = m.Location?.ReceivedAtUtc
                }).ToList();
            }
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
