using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Delivery;
using APIBack.Extensions;
using APIBack.Repository.Interface;
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
        private readonly IOperationalSessionService _sessionService;
        private readonly IPedidoHistoricoRepository _historicoRepository;
        private readonly IRestaurantSettingsRepository _restaurantRepository;

        public DeliveryOrdersV2Controller(
            IPedidoQueueService queueService,
            IOperationalSessionService sessionService,
            IPedidoHistoricoRepository historicoRepository,
            IRestaurantSettingsRepository restaurantRepository)
        {
            _queueService = queueService;
            _sessionService = sessionService;
            _historicoRepository = historicoRepository;
            _restaurantRepository = restaurantRepository;
        }

        // ---- Historico do pedido ------------------------------------------------

        /// <summary>Linha do tempo do pedido: atribuicoes, coleta, chegada, desfecho e transferencias.</summary>
        [HttpGet("pedidos/{pedidoId:int}/historico")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> GetHistorico(int pedidoId) =>
            ExecuteAsync(async (est, _) =>
                await _historicoRepository.GetAsync(est, pedidoId)
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado."));

        // ---- Pedido manual ------------------------------------------------------

        [HttpPost("pedidos")]
        [RequirePermission("Delivery", "criar_pedido")]
        public Task<IActionResult> CreatePedido([FromBody] CreatePedidoRequest request) =>
            ExecuteAsync((est, userId) => _queueService.CreatePedidoAsync(est, userId, request), created: true);

        // ---- Transferencia ------------------------------------------------------

        /// <summary>Atendente move o pedido para outro motoboy (sempre direto, sem aprovacao).</summary>
        [HttpPost("pedidos/{pedidoId:int}/transferir")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public Task<IActionResult> TransferPedido(int pedidoId, [FromBody] TransferPedidoRequest request) =>
            ExecuteAsync((est, userId) => _queueService.TransferByOperatorAsync(est, userId, pedidoId, request.ParaMotoboyId, request.Motivo));

        [HttpGet("transferencias")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> ListTransfers([FromQuery] string? status, [FromQuery] int limit = 50) =>
            ExecuteAsync((est, _) => _queueService.ListTransfersAsync(est, status, limit));

        [HttpPost("transferencias/{transferId:long}/aprovar")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public Task<IActionResult> ApproveTransfer(long transferId, [FromBody] TransferDecisionRequest? request) =>
            ExecuteAsync((est, userId) => _queueService.ApproveTransferAsync(est, userId, transferId, request?.Observacao));

        [HttpPost("transferencias/{transferId:long}/rejeitar")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public Task<IActionResult> RejectTransfer(long transferId, [FromBody] TransferDecisionRequest? request) =>
            ExecuteAsync((est, userId) => _queueService.RejectTransferAsync(est, userId, transferId, request?.Observacao));

        // ---- Trajeto do dia -----------------------------------------------------

        [HttpGet("motoboys/{motoboyId:int}/trajeto")]
        [RequirePermission("Delivery", "visualizar")]
        public async Task<IActionResult> GetTrajectory(int motoboyId, [FromQuery] string? date)
        {
            var localDate = OperationalDayWindow.Today(DateTimeOffset.UtcNow);
            if (!string.IsNullOrWhiteSpace(date) && !DateOnly.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, out localDate))
            {
                return BadRequest(ApiResponse<object>.Fail("Parametro date invalido. Use YYYY-MM-DD.", "INVALID_REQUEST"));
            }
            return await ExecuteAsync((est, _) => _sessionService.GetTrajectoryAsync(est, motoboyId, localDate));
        }

        // ---- Parametros do estabelecimento (a tela de parametros vira depois) ----

        [HttpGet("configuracoes")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> GetSettings() =>
            ExecuteAsync((est, _) => _queueService.GetSettingsAsync(est));

        /// <summary>Endereco, posicao no mapa, raio e regras de entrega do restaurante.</summary>
        [HttpGet("configuracoes/restaurante")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> GetRestaurantSettings() =>
            ExecuteAsync(async (est, _) =>
                await _restaurantRepository.GetAsync(est)
                ?? throw new DeliveryDomainException(404, "ESTABELECIMENTO_NOT_FOUND", "Estabelecimento nao encontrado."));

        [HttpPut("configuracoes/restaurante")]
        [RequirePermission("Delivery", "configurar")]
        public Task<IActionResult> UpdateRestaurantSettings([FromBody] UpdateRestaurantSettingsRequest request) =>
            ExecuteAsync(async (est, _) =>
                await _restaurantRepository.UpdateAsync(est, RestaurantSettingsRules.Validate(request))
                ?? throw new DeliveryDomainException(404, "ESTABELECIMENTO_NOT_FOUND", "Estabelecimento nao encontrado."));

        [HttpPut("configuracoes")]
        [RequirePermission("Delivery", "configurar")]
        public Task<IActionResult> UpdateSettings([FromBody] UpdateDeliverySettingsRequest request) =>
            ExecuteAsync((est, userId) => _queueService.UpdateSettingsAsync(est, userId, request));

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

        private async Task<IActionResult> ExecuteAsync<T>(Func<Guid, int, Task<T>> action, bool created = false)
        {
            if (!TryGetActor(out var actorUserId, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var result = await action(estabelecimentoId, actorUserId);
                return created
                    ? StatusCode(201, ApiResponse<T>.Ok(result))
                    : Ok(ApiResponse<T>.Ok(result));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        private IActionResult DomainError(DeliveryDomainException ex) =>
            StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details));
    }
}
