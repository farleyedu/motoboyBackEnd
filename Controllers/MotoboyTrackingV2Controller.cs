using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Tracking;
using APIBack.Extensions;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace APIBack.Controllers
{
    [Route("api/v2/motoboys/me/session")]
    [ApiController]
    public sealed class MotoboyTrackingV2Controller : ControllerBase
    {
        private readonly IOperationalSessionService _service;

        public MotoboyTrackingV2Controller(IOperationalSessionService service)
        {
            _service = service;
        }

        [HttpPost("start")]
        public async Task<IActionResult> Start([FromBody] StartOperationalSessionRequest request)
        {
            var userId = HttpContext.GetUserId() ?? 0;
            var estabelecimentoId = HttpContext.GetEstabelecimentoId() ?? Guid.Empty;
            if (userId <= 0 || estabelecimentoId == Guid.Empty)
            {
                return Unauthorized(ApiResponse<object>.Fail("Contexto autenticado invalido.", "UNAUTHENTICATED"));
            }

            return await ExecuteAsync(async () =>
                ApiResponse<OperationalSessionTokenResponse>.Ok(
                    await _service.StartMobileSessionAsync(userId, estabelecimentoId, request)));
        }

        [HttpPost("switch")]
        public async Task<IActionResult> Switch([FromBody] SwitchOperationalSessionRequest request)
        {
            var userId = HttpContext.GetUserId() ?? 0;
            if (userId <= 0)
            {
                return Unauthorized(ApiResponse<object>.Fail("Usuario nao autenticado.", "UNAUTHENTICATED"));
            }

            return await ExecuteAsync(async () =>
                ApiResponse<OperationalSessionTokenResponse>.Ok(
                    await _service.SwitchMobileSessionAsync(userId, request)));
        }

        [HttpGet]
        [RequireOperationalSession]
        public async Task<IActionResult> GetCurrent() =>
            await ExecuteAsync(async () =>
                ApiResponse<OperationalSessionDto?>.Ok(
                    await _service.GetSessionAsync(HttpContext.GetJwtPayload())));

        [HttpPost("heartbeat")]
        [RequireOperationalSession]
        public async Task<IActionResult> Heartbeat() =>
            await ExecuteAsync(async () =>
                ApiResponse<OperationalHeartbeatResponse>.Ok(
                    await _service.HeartbeatAsync(HttpContext.GetJwtPayload())));

        [HttpPost("location")]
        [RequireOperationalSession]
        [EnableRateLimiting("delivery-location")]
        [RequestSizeLimit(16_384)]
        public async Task<IActionResult> SendLocation([FromBody] OperationalLocationRequest request) =>
            await ExecuteAsync(async () =>
                ApiResponse<OperationalLocationAckDto>.Ok(
                    await _service.ReceiveLocationAsync(HttpContext.GetJwtPayload(), request)));

        [HttpDelete]
        [RequireOperationalSession]
        public async Task<IActionResult> End([FromQuery] string? reason) =>
            await ExecuteAsync(async () =>
                ApiResponse<OperationalSessionDto>.Ok(
                    await _service.EndSessionAsync(HttpContext.GetJwtPayload(), reason ?? "client_end")));

        private async Task<IActionResult> ExecuteAsync<T>(Func<Task<ApiResponse<T>>> action)
        {
            try
            {
                return Ok(await action());
            }
            catch (DeliveryDomainException ex)
            {
                return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details));
            }
        }
    }
}
