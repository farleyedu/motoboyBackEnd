using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Atendimento;
using APIBack.DTOs.Common;
using APIBack.DTOs.Rastreio;
using APIBack.Repository.Interface;
using APIBack.DTOs.Delivery;
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
        private readonly IPedidoQueueService _queueService;
        private readonly AtendimentoService _atendimento;
        private readonly IRastreioRepository _rastreio;
        private readonly MotoboyPedidoService _pedido;

        public MotoboyTrackingV2Controller(IOperationalSessionService service, IPedidoQueueService queueService, AtendimentoService atendimento, IRastreioRepository rastreio, MotoboyPedidoService pedido)
        {
            _pedido = pedido;
            _rastreio = rastreio;
            _service = service;
            _queueService = queueService;
            _atendimento = atendimento;
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

        [HttpGet("queue")]
        [RequireOperationalSession]
        public async Task<IActionResult> GetQueue()
        {
            var payload = HttpContext.GetJwtPayload();
            var estabelecimentoId = HttpContext.GetEstabelecimentoId() ?? Guid.Empty;
            if (!payload.MotoboyId.HasValue || estabelecimentoId == Guid.Empty)
            {
                return Unauthorized(ApiResponse<object>.Fail("Contexto operacional invalido.", "OPERATIONAL_TOKEN_REQUIRED"));
            }

            return await ExecuteAsync(async () =>
                ApiResponse<MotoboyQueueDto>.Ok(
                    await _queueService.GetQueueAsync(estabelecimentoId, payload.MotoboyId.Value)));
        }

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

        // =====================================================================
        // Acoes do motoboy sobre a propria fila. Motoboy e estabelecimento vem do
        // token operacional; nenhum ID arbitrario de motoboy e aceito.
        // =====================================================================

        [HttpPost("stops/current/pickup")]
        [RequireOperationalSession]
        public Task<IActionResult> PickUp() =>
            WithOperationalContextAsync((est, motoboyId, _) => _queueService.MarkPickedUpAsync(est, motoboyId));

        [HttpPost("stops/current/arrive")]
        [RequireOperationalSession]
        public Task<IActionResult> Arrive([FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] ArrivalStopRequest? request) =>
            WithOperationalContextAsync((est, motoboyId, _) => request?.ExpectedPedidoId is int pedidoId
                ? _queueService.MarkArrivedForPedidoAsync(est, motoboyId, pedidoId)
                : _queueService.MarkArrivedAsync(est, motoboyId));

        [HttpPost("stops/current/deliver")]
        [RequireOperationalSession]
        public Task<IActionResult> Deliver([FromBody] DeliverStopRequest? request) =>
            WithOperationalContextAsync((est, motoboyId, _) => _queueService.DeliverCurrentAsync(est, motoboyId, request?.Codigo));

        [HttpPost("stops/current/fail")]
        [RequireOperationalSession]
        public Task<IActionResult> Fail([FromBody] FailStopRequest? request) =>
            WithOperationalContextAsync((est, motoboyId, _) => _queueService.FailCurrentAsync(est, motoboyId, request?.Motivo));

        [HttpPost("stops/{pedidoId:int}/refuse")]
        [RequireOperationalSession]
        public Task<IActionResult> Refuse(int pedidoId, [FromBody] RefuseStopRequest? request) =>
            WithOperationalContextAsync((est, motoboyId, _) => _queueService.RefuseAsync(est, motoboyId, pedidoId, request?.Motivo));

        [HttpPost("queue/offer/accept")]
        [RequireOperationalSession]
        public Task<IActionResult> AcceptOffer() =>
            WithOperationalContextAsync((est, motoboyId, _) => _queueService.AcceptOfferAsync(est, motoboyId));

        [HttpPost("queue/offer/reject")]
        [RequireOperationalSession]
        public Task<IActionResult> RejectOffer([FromBody] RejectOfferRequest? request) =>
            WithOperationalContextAsync((est, motoboyId, _) => _queueService.RejectOfferAsync(est, motoboyId, request?.Motivo));

        [HttpPut("queue/reorder")]
        [RequireOperationalSession]
        public Task<IActionResult> Reorder([FromBody] ReorderQueueRequest request) =>
            WithOperationalContextAsync((est, motoboyId, _) =>
                _queueService.ReorderByMotoboyAsync(est, motoboyId, request.ExpectedVersion, request.PedidoIdsOrdenados));

        [HttpPost("queue/resume")]
        [RequireOperationalSession]
        public Task<IActionResult> ResumeQueue() =>
            WithOperationalContextAsync((est, motoboyId, _) => _queueService.ResumeByMotoboyAsync(est, motoboyId));

        /// <summary>Cheguei a loja: encerra o retorno da rota (alternativa ao raio automatico).</summary>
        [HttpPost("queue/arrived-at-store")]
        [RequireOperationalSession]
        public Task<IActionResult> ArrivedAtStore() =>
            WithOperationalContextAsync((est, motoboyId, _) => _queueService.ArriveAtStoreAsync(est, motoboyId));

        // ---- Preferencias do motoboy (Fase 6) --------------------------------------

        /// <summary>O motoboy autorizou mostrar sua posicao ao cliente no link de rastreio?</summary>
        [HttpGet("preferences")]
        [RequireOperationalSession]
        public Task<IActionResult> GetPreferences() =>
            WithOperationalContextAsync(async (_, motoboyId, _) =>
                new MotoboyPreferencesDto { CompartilharLocalizacaoCliente = await _rastreio.GetMotoboySharingAsync(motoboyId) });

        [HttpPatch("preferences")]
        [RequireOperationalSession]
        public Task<IActionResult> SetPreferences([FromBody] MotoboyPreferencesRequest? request) =>
            WithOperationalContextAsync(async (_, motoboyId, _) =>
            {
                if (request == null) throw new DeliveryDomainException(400, "INVALID_REQUEST", "Corpo da requisicao obrigatorio.");
                await _rastreio.SetMotoboySharingAsync(motoboyId, request.CompartilharLocalizacaoCliente);
                return new MotoboyPreferencesDto { CompartilharLocalizacaoCliente = request.CompartilharLocalizacaoCliente };
            });

        // ---- Mensagens com o atendente (Fase 5) ----------------------------------

        [HttpGet("messages")]
        [RequireOperationalSession]
        public Task<IActionResult> GetMessages([FromQuery] int limit = 50) =>
            WithOperationalContextAsync((est, motoboyId, _) => _atendimento.ListMessagesAsync(est, motoboyId, null, limit));

        [HttpPost("messages")]
        [RequireOperationalSession]
        public Task<IActionResult> SendMessage([FromBody] SendMotoboyMessageRequest? request) =>
            WithOperationalContextAsync((est, motoboyId, _) => _atendimento.SendFromMotoboyAsync(est, motoboyId, request));

        [HttpPost("messages/read")]
        [RequireOperationalSession]
        public Task<IActionResult> ReadMessages() =>
            WithOperationalContextAsync(async (est, motoboyId, _) => new { marcadas = await _atendimento.MarkReadAsync(est, motoboyId, readerIsMotoboy: true) });

        [HttpGet("message-shortcuts")]
        [RequireOperationalSession]
        public IActionResult MessageShortcuts() => Ok(ApiResponse<object>.Ok(AtendimentoService.Shortcuts(operatorSide: false)));

        // ---- Mensagem ao cliente do pedido (Fase D) ------------------------------

        /// <summary>Itens e fotos do catálogo do pedido na própria fila, sem revelar o código de entrega.</summary>
        [HttpGet("orders/{pedidoId:int}")]
        [RequireOperationalSession]
        public Task<IActionResult> GetOrder(int pedidoId) =>
            WithOperationalContextAsync((est, motoboyId, _) => _pedido.GetAsync(est, motoboyId, pedidoId));

        [HttpGet("orders/{pedidoId:int}/client-channel")]
        [RequireOperationalSession]
        public Task<IActionResult> GetClientChannel(int pedidoId) =>
            WithOperationalContextAsync(async (est, motoboyId, _) =>
            {
                await _pedido.GetAsync(est, motoboyId, pedidoId);
                return await _atendimento.GetCanaisAsync(est, new PedidosCanaisRequest { PedidoIds = new() { pedidoId } });
            });

        [HttpGet("contacts")]
        [RequireOperationalSession]
        public Task<IActionResult> GetContacts() =>
            WithOperationalContextAsync((est, _, __) => _atendimento.ListMotoboysAsync(est));

        [HttpPost("orders/{pedidoId:int}/client-messages")]
        [RequireOperationalSession]
        public Task<IActionResult> SendClientMessage(int pedidoId, [FromBody] SendMotoboyClientMessageRequest? request) =>
            WithOperationalContextAsync((est, motoboyId, _) => _atendimento.SendToClientAsync(est, motoboyId, pedidoId, request));

        [HttpGet("orders/{pedidoId:int}/client-messages")]
        [RequireOperationalSession]
        public Task<IActionResult> GetClientMessages(int pedidoId, [FromQuery] DateTime? before, [FromQuery] int limit = 50) =>
            WithOperationalContextAsync((est, motoboyId, _) => _atendimento.ListClientMessagesAsync(est, motoboyId, pedidoId, before, limit));

        // ---- Grupo de motoboys da loja (Fase E) ----------------------------------

        [HttpGet("group-messages")]
        [RequireOperationalSession]
        public Task<IActionResult> GetGroupMessages([FromQuery] int limit = 50) =>
            WithOperationalContextAsync((est, _, __) => _atendimento.ListGroupMessagesAsync(est, limit));

        [HttpPost("group-messages")]
        [RequireOperationalSession]
        public Task<IActionResult> SendGroupMessage([FromBody] SendMotoboyGroupMessageRequest? request) =>
            WithOperationalContextAsync((est, motoboyId, _) => _atendimento.SendGroupMessageFromMotoboyAsync(est, motoboyId, request));

        [HttpGet("transfer-targets")]
        [RequireOperationalSession]
        public Task<IActionResult> GetTransferTargets() =>
            WithOperationalContextAsync((est, motoboyId, _) => _queueService.GetTransferTargetsAsync(est, motoboyId));

        [HttpPost("stops/{pedidoId:int}/transfer")]
        [RequireOperationalSession]
        public Task<IActionResult> Transfer(int pedidoId, [FromBody] TransferPedidoRequest request) =>
            WithOperationalContextAsync((est, motoboyId, userId) =>
                _queueService.RequestTransferByMotoboyAsync(est, motoboyId, userId, pedidoId, request.ParaMotoboyId, request.Motivo));

        [HttpGet("transfers")]
        [RequireOperationalSession]
        public Task<IActionResult> GetTransfers([FromQuery] int limit = 20) =>
            WithOperationalContextAsync((est, motoboyId, _) => _queueService.ListMotoboyTransfersAsync(est, motoboyId, limit));

        [HttpDelete("transfers/{transferId:long}")]
        [RequireOperationalSession]
        public Task<IActionResult> CancelTransfer(long transferId) =>
            WithOperationalContextAsync((est, motoboyId, _) => _queueService.CancelTransferByMotoboyAsync(est, motoboyId, transferId));

        private async Task<IActionResult> WithOperationalContextAsync<T>(Func<Guid, int, int, Task<T>> action)
        {
            var payload = HttpContext.GetJwtPayload();
            var estabelecimentoId = HttpContext.GetEstabelecimentoId() ?? Guid.Empty;
            var userId = HttpContext.GetUserId() ?? 0;
            if (!payload.MotoboyId.HasValue || estabelecimentoId == Guid.Empty)
            {
                return Unauthorized(ApiResponse<object>.Fail("Contexto operacional invalido.", "OPERATIONAL_TOKEN_REQUIRED"));
            }

            return await ExecuteAsync(async () =>
                ApiResponse<T>.Ok(await action(estabelecimentoId, payload.MotoboyId.Value, userId)));
        }

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
