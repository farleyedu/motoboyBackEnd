using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Delivery;
using APIBack.Extensions;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    /// <summary>
    /// Comandos transacionais de fila/rota (Parte 2 do plano de delivery), usados pelo
    /// painel do atendente. O motoboy/app le a propria fila via
    /// MotoboyTrackingV2Controller.GetQueue (token operacional, sem ID arbitrario).
    /// </summary>
    [Route("api/v2/delivery")]
    [ApiController]
    public sealed class DeliveryOrdersV2Controller : ControllerBase
    {
        private readonly IPedidoQueueService _queueService;

        public DeliveryOrdersV2Controller(IPedidoQueueService queueService)
        {
            _queueService = queueService;
        }

        [HttpPost("pedidos/{pedidoId:int}/atribuir")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public async Task<IActionResult> Assign(int pedidoId, [FromBody] AssignPedidoRequest request)
        {
            if (!TryGetActor(out var actorUserId, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var snapshot = await _queueService.AssignAsync(estabelecimentoId, actorUserId, request.MotoboyId, pedidoId);
                return Ok(ApiResponse<MotoboyQueueDto>.Ok(snapshot));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        [HttpDelete("pedidos/{pedidoId:int}/fila")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public async Task<IActionResult> Remove(int pedidoId)
        {
            if (!TryGetActor(out var actorUserId, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var snapshot = await _queueService.RemoveAsync(estabelecimentoId, actorUserId, pedidoId);
                return Ok(ApiResponse<MotoboyQueueDto>.Ok(snapshot));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        [HttpPost("pedidos/{pedidoId:int}/cancelar")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public async Task<IActionResult> Cancel(int pedidoId, [FromBody] CancelPedidoRequest request)
        {
            if (!TryGetActor(out var actorUserId, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var snapshot = await _queueService.CancelAsync(estabelecimentoId, actorUserId, pedidoId, request?.Motivo);
                return Ok(ApiResponse<MotoboyQueueDto>.Ok(snapshot));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        [HttpGet("motoboys/{motoboyId:int}/fila")]
        [RequirePermission("Delivery", "visualizar")]
        public async Task<IActionResult> GetQueue(int motoboyId)
        {
            if (!TryGetActor(out _, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var snapshot = await _queueService.GetQueueAsync(estabelecimentoId, motoboyId);
                return Ok(ApiResponse<MotoboyQueueDto>.Ok(snapshot));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        [HttpPut("motoboys/{motoboyId:int}/fila/reordenar")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public async Task<IActionResult> Reorder(int motoboyId, [FromBody] ReorderQueueRequest request)
        {
            if (!TryGetActor(out var actorUserId, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var snapshot = await _queueService.ReorderAsync(
                    estabelecimentoId, actorUserId, motoboyId, request.ExpectedVersion, request.PedidoIdsOrdenados);
                return Ok(ApiResponse<MotoboyQueueDto>.Ok(snapshot));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        [HttpPost("motoboys/{motoboyId:int}/fila/concluir-atual")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public async Task<IActionResult> CompleteCurrent(int motoboyId)
        {
            if (!TryGetActor(out var actorUserId, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var snapshot = await _queueService.CompleteCurrentAsync(estabelecimentoId, actorUserId, motoboyId);
                return Ok(ApiResponse<MotoboyQueueDto>.Ok(snapshot));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        [HttpPost("motoboys/{motoboyId:int}/fila/retomar")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public async Task<IActionResult> Resume(int motoboyId)
        {
            if (!TryGetActor(out var actorUserId, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var snapshot = await _queueService.ResumeAsync(estabelecimentoId, actorUserId, motoboyId);
                return Ok(ApiResponse<MotoboyQueueDto>.Ok(snapshot));
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
