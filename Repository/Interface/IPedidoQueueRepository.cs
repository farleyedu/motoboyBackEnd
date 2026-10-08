using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;

namespace APIBack.Repository.Interface
{
    public interface IPedidoQueueRepository
    {
        Task<DeliveryCompletionContext> GetCompletionContextAsync(Guid store, int rider, int pedido, CancellationToken ct);
        Task<bool> ValidateCompletionCodeAsync(Guid store, int rider, int pedido, string code, CancellationToken ct);
        Task<Guid> SaveCompletionProofAsync(Guid store, int rider, int pedido, string base64, CancellationToken ct);
        Task<string?> ReadPendingCompletionProofAsync(Guid store, int rider, int pedido, CancellationToken ct);
        Task<DeliveryCompletionResult> CompleteDeliveryAsync(Guid store, int rider, int user, Guid session, long epoch, DeliveryCompletionRequest request, CancellationToken ct);
        Task<DeliveryReceipt?> GetDeliveryReceiptAsync(int user, Guid operation, CancellationToken ct);
        Task<string?> GetDeliveryProofAsync(int user, Guid operation, CancellationToken ct);
        // Atendente
        Task<MotoboyQueueDto> AssignAsync(Guid estabelecimentoId, int actorUserId, int motoboyId, int pedidoId);
        /// <summary>Para autoatribuicao: so devolve um motoboy quando ha EXATAMENTE um disponivel (online,
        /// sem parada assigned/en_route) - com 0 ou mais de 1, devolve null e o pedido fica na fila normal.</summary>
        Task<int?> FindSingleAvailableMotoboyAsync(Guid estabelecimentoId);
        Task<MotoboyQueueDto> RemoveAsync(Guid estabelecimentoId, int actorUserId, int pedidoId);
        Task<MotoboyQueueDto> ReorderAsync(Guid estabelecimentoId, int actorUserId, int motoboyId, long expectedVersion, IReadOnlyList<int> pedidoIdsOrdenados);
        Task<MotoboyQueueDto> CompleteCurrentAsync(Guid estabelecimentoId, int actorUserId, int motoboyId);
        Task<MotoboyQueueDto> ResumeAsync(Guid estabelecimentoId, int actorUserId, int motoboyId);
        Task<MotoboyQueueDto> CancelAsync(Guid estabelecimentoId, int actorUserId, int pedidoId, string? motivo);
        Task<MotoboyQueueDto> GetQueueAsync(Guid estabelecimentoId, int motoboyId, CancellationToken cancellationToken = default);
        /// <summary>Trava/destrava pedidos da fila (ancoras). Estabelecimento apenas.</summary>
        Task<LockPedidosResultDto> SetLockedAsync(Guid estabelecimentoId, int actorUserId, IReadOnlyList<int> pedidoIds, bool locked);
        /// <summary>Encerra o retorno a loja por acao manual (motoboy ou atendente). Idempotente.</summary>
        Task<MotoboyQueueDto> ArriveAtStoreAsync(Guid estabelecimentoId, int motoboyId);
        /// <summary>Encerra o retorno se a posicao esta no raio da loja e o estabelecimento permite. Nunca lanca por falha propria.</summary>
        Task<bool> TryFinishReturnByLocationAsync(Guid estabelecimentoId, int motoboyId, double latitude, double longitude);

        // Motoboy
        Task<MotoboyQueueDto> MarkPickedUpAsync(Guid estabelecimentoId, int motoboyId);
        Task<MotoboyQueueDto> PickUpStopsAsync(Guid estabelecimentoId, int motoboyId, PickupStopsRequest request);
        Task<MotoboyQueueDto> AcceptOfferForAsync(Guid estabelecimentoId, int motoboyId, Guid expectedOfferId);
        Task<MotoboyQueueDto> AcceptPricedOfferAsync(Guid estabelecimentoId, int motoboyId, Guid expectedOfferId, long expectedVersion) => throw new NotSupportedException();
        Task<MotoboyQueueDto> RejectOfferForAsync(Guid estabelecimentoId, int motoboyId, Guid expectedOfferId, string? motivo);
        Task<MotoboyQueueDto> PauseTurnAsync(Guid estabelecimentoId, int motoboyId, Guid sessionId, long sessionEpoch, bool paused);
        Task<MotoboyQueueDto> MarkArrivedAsync(Guid estabelecimentoId, int motoboyId);
        Task<MotoboyQueueDto> MarkArrivedForPedidoAsync(Guid estabelecimentoId, int motoboyId, int expectedPedidoId);
        Task<MotoboyQueueDto> DeliverCurrentAsync(Guid estabelecimentoId, int motoboyId, string? codigo);
        Task<MotoboyQueueDto> FailCurrentAsync(Guid estabelecimentoId, int motoboyId, string motivo);
        Task<MotoboyQueueDto> FailCurrentForPedidoAsync(Guid estabelecimentoId, int motoboyId, int expectedPedidoId, string motivo);
        Task<MotoboyQueueDto> RefuseAsync(Guid estabelecimentoId, int motoboyId, int pedidoId, string? motivo);
        Task<MotoboyQueueDto> RefuseRouteByMotoboyAsync(Guid estabelecimentoId, int motoboyId, IReadOnlyList<int> pedidoIds, string? motivo);
        Task<MotoboyQueueDto> ReorderByMotoboyAsync(Guid estabelecimentoId, int motoboyId, long expectedVersion, IReadOnlyList<int> pedidoIdsOrdenados);
        Task<MotoboyQueueDto> AcceptRouteByMotoboyAsync(Guid estabelecimentoId, int motoboyId, long expectedVersion, IReadOnlyList<int> pedidoIdsOrdenados);

        // Transferencia
        Task<IReadOnlyList<TransferTargetDto>> GetTransferTargetsAsync(Guid estabelecimentoId, int motoboyId);
        Task<TransferResultDto> RequestTransferByMotoboyAsync(Guid estabelecimentoId, int fromMotoboyId, int actorUserId, int pedidoId, int toMotoboyId, string? motivo);
        Task<TransferResultDto> TransferByOperatorAsync(Guid estabelecimentoId, int operatorUserId, int pedidoId, int toMotoboyId, string? motivo);
        Task<TransferResultDto> ApproveTransferAsync(Guid estabelecimentoId, int operatorUserId, long requestId, string? observacao);
        Task<TransferRequestDto> RejectTransferAsync(Guid estabelecimentoId, int operatorUserId, long requestId, string? observacao);
        Task<TransferRequestDto> CancelTransferByMotoboyAsync(Guid estabelecimentoId, int motoboyId, long requestId);
        Task<IReadOnlyList<TransferRequestDto>> ListTransfersAsync(Guid estabelecimentoId, string? status, int limit);
        Task<IReadOnlyList<TransferRequestDto>> ListMotoboyTransfersAsync(Guid estabelecimentoId, int motoboyId, int limit);

        // Pedido manual
        Task<CreatedPedidoDto> CreatePedidoAsync(Guid estabelecimentoId, int actorUserId, APIBack.Service.ManualOrder order);
        Task<CreatedPedidoDto> UpdatePedidoAsync(Guid estabelecimentoId, int actorUserId, int pedidoId, APIBack.Service.ManualOrder order);
        Task<CreatedPedidoDto> ConfirmPedidoAsync(Guid estabelecimentoId, int actorUserId, int pedidoId, int previsaoMinutos);
        Task<CreatedPedidoDto> UpdatePedidoForSimulatorAsync(Guid estabelecimentoId, int actorUserId, int pedidoId, APIBack.Service.SimulatorOrderPatch patch);
        Task<CreatedPedidoDto> ReopenPedidoForSimulatorAsync(Guid estabelecimentoId, int actorUserId, int pedidoId);

        // ---- Confirmacao do motoboy (oferta de rota) ----
        Task<MotoboyQueueDto> AssignRouteAsync(Guid estabelecimentoId, int actorUserId, int motoboyId, IReadOnlyList<int> pedidoIdsOrdenados);
        Task<MotoboyQueueDto> AcceptOfferAsync(Guid estabelecimentoId, int motoboyId);
        Task<MotoboyQueueDto> RejectOfferAsync(Guid estabelecimentoId, int motoboyId, string? motivo);
        Task<int> ExpireOffersAsync(DateTimeOffset agoraUtc);

        // ---- Encerramento automatico de pedidos em aberto ----
        Task<APIBack.Service.EncerramentoSettings> GetEncerramentoSettingsAsync(Guid estabelecimentoId);
        Task<IReadOnlyList<Guid>> ListEstablishmentsWithOpenOrdersAsync();
        Task<IReadOnlyList<APIBack.Service.EncerramentoCandidate>> ListOpenOrdersForEncerramentoAsync(Guid estabelecimentoId);
        Task<bool> EncerrarPedidoAsync(Guid estabelecimentoId, int pedidoId, string motivo, int? actorUserId);
        Task<CreatedPedidoDto> ReabrirEncerradoAsync(Guid estabelecimentoId, int actorUserId, int pedidoId);
        Task PublishPedidoEventForSimulatorAsync(Guid estabelecimentoId, int actorUserId, int pedidoId, string action);

        // Parametros
        Task<DeliverySettingsDto> GetSettingsAsync(Guid estabelecimentoId);
        Task<DeliverySettingsDto> UpsertSettingsAsync(Guid estabelecimentoId, int actorUserId, UpdateDeliverySettingsRequest request);
    }
}
