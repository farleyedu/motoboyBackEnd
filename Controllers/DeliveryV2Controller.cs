using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Tracking;
using APIBack.Extensions;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    [Route("api/v2/delivery")]
    [ApiController]
    public sealed class DeliveryV2Controller : ControllerBase
    {
        private readonly IOperationalSessionService _service;

        public DeliveryV2Controller(IOperationalSessionService service)
        {
            _service = service;
        }

        [HttpGet("simulator/candidates")]
        [RequirePermission("Delivery", "gestao_motoboy")]
        public async Task<IActionResult> GetSimulatorCandidates()
        {
            if (!TryGetActor(out _, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var candidates = await _service.GetSimulatorCandidatesAsync(estabelecimentoId);
                return Ok(ApiResponse<SimulatorCandidatesResponse>.Ok(new SimulatorCandidatesResponse
                {
                    EstablishmentId = estabelecimentoId,
                    Candidates = candidates
                }));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        [HttpPost("simulator/sessions/auto-start")]
        [RequirePermission("Delivery", "gestao_motoboy")]
        public async Task<IActionResult> AutoStartSimulator([FromBody] StartSimulatorSessionRequest request)
        {
            if (!TryGetActor(out var actorUserId, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var response = await _service.AutoStartSimulatorSessionAsync(actorUserId, estabelecimentoId, request);
                return Ok(ApiResponse<SimulatorAutoStartResponse>.Ok(response));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        [HttpPost("simulator/motoboys")]
        [RequirePermission("Delivery", "gestao_motoboy")]
        public async Task<IActionResult> CreateSimulatorMotoboy([FromBody] CreateSimulatorMotoboyRequest request)
        {
            if (!TryGetActor(out _, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var response = await _service.CreateSimulatorMotoboyAsync(estabelecimentoId, request);
                return StatusCode(201, ApiResponse<MotoboyMapDto>.Ok(response));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        [HttpGet("tracking-snapshot")]
        [RequirePermission("Delivery", "visualizar")]
        public async Task<IActionResult> GetTrackingSnapshot()
        {
            if (!TryGetActor(out _, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var response = await _service.GetSnapshotAsync(estabelecimentoId);
                return Ok(ApiResponse<DeliveryTrackingSnapshotDto>.Ok(response));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        private bool TryGetActor(out int userId, out Guid estabelecimentoId, out IActionResult? error)
        {
            userId = HttpContext.GetUserId() ?? 0;
            estabelecimentoId = HttpContext.GetEstabelecimentoId() ?? Guid.Empty;
            if (userId <= 0 || estabelecimentoId == Guid.Empty)
            {
                error = Unauthorized(ApiResponse<object>.Fail("Contexto autenticado invalido.", "UNAUTHENTICATED"));
                return false;
            }

            error = null;
            return true;
        }

        private IActionResult DomainError(DeliveryDomainException ex) =>
            StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details));
    }
}
