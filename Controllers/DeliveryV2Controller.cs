using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Clientes;
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
        private readonly IClienteSimulatorService _clientSimulator;

        public DeliveryV2Controller(IOperationalSessionService service, IClienteSimulatorService clientSimulator)
        {
            _service = service;
            _clientSimulator = clientSimulator;
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
            if (!TryGetActor(out var actorUserId, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var response = await _service.CreateSimulatorMotoboyAsync(
                    actorUserId, HttpContext.IsSuperAdmin(), estabelecimentoId, request);
                return StatusCode(201, ApiResponse<MotoboyMapDto>.Ok(response));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        /// <summary>Edita nome e telefone de um motoboy de teste.</summary>
        [HttpPut("simulator/motoboys/{motoboyId:int}")]
        [RequirePermission("Delivery", "gestao_motoboy")]
        public async Task<IActionResult> UpdateSimulatorMotoboy(int motoboyId, [FromBody] UpdateSimulatorMotoboyRequest request)
        {
            if (!TryGetActor(out _, out var estabelecimentoId, out var error)) return error!;
            try
            {
                await _service.UpdateSimulatorMotoboyAsync(estabelecimentoId, motoboyId, request);
                return Ok(ApiResponse<object>.Ok(new { id = motoboyId }));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        /// <summary>Remove um motoboy de teste do estabelecimento (recusa se ainda tem pedido ativo).</summary>
        [HttpDelete("simulator/motoboys/{motoboyId:int}")]
        [RequirePermission("Delivery", "gestao_motoboy")]
        public async Task<IActionResult> RemoveSimulatorMotoboy(int motoboyId)
        {
            if (!TryGetActor(out _, out var estabelecimentoId, out var error)) return error!;
            try
            {
                await _service.RemoveSimulatorMotoboyAsync(estabelecimentoId, motoboyId);
                return Ok(ApiResponse<object>.Ok(new { id = motoboyId }));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        /// <summary>Clientes de TESTE do estabelecimento que o simulador pode encarnar.</summary>
        [HttpGet("simulator/clientes")]
        [RequirePermission("Delivery", "gestao_motoboy")]
        public async Task<IActionResult> GetSimulatorClientes()
        {
            if (!TryGetActor(out _, out var estabelecimentoId, out var error)) return error!;
            try
            {
                return Ok(ApiResponse<object>.Ok(await _clientSimulator.ListAsync(estabelecimentoId)));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        /// <summary>O cliente de teste manda uma mensagem: entra pelo mesmo POST /wa/webhook do WhatsApp.</summary>
        [HttpPost("simulator/clientes/{clienteId:guid}/mensagens")]
        [RequirePermission("Delivery", "gestao_motoboy")]
        public async Task<IActionResult> SendSimulatorClienteMessage(Guid clienteId, [FromBody] SimulatorClienteMessageRequest request)
        {
            if (!TryGetActor(out _, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var result = await _clientSimulator.SendMessageAsync(estabelecimentoId, clienteId, request?.Texto);
                return StatusCode(202, ApiResponse<SimulatedSendResult>.Ok(result));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        /// <summary>Conversa do cliente de teste (o que ele mandou e o que o atendimento respondeu).</summary>
        [HttpGet("simulator/clientes/{clienteId:guid}/mensagens")]
        [RequirePermission("Delivery", "gestao_motoboy")]
        public async Task<IActionResult> GetSimulatorClienteMessages(Guid clienteId, [FromQuery] int limit = 100)
        {
            if (!TryGetActor(out _, out var estabelecimentoId, out var error)) return error!;
            try
            {
                return Ok(ApiResponse<object>.Ok(await _clientSimulator.GetMessagesAsync(estabelecimentoId, clienteId, limit)));
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
