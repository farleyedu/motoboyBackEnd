using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;

namespace APIBack.Service.Interface
{
    public interface IPedidoQueueService
    {
        // Atendente
        Task<MotoboyQueueDto> AssignAsync(Guid estabelecimentoId, int actorUserId, int motoboyId, int pedidoId);
        Task<MotoboyQueueDto> RemoveAsync(Guid estabelecimentoId, int actorUserId, int pedidoId);
        Task<MotoboyQueueDto> ReorderAsync(Guid estabelecimentoId, int actorUserId, int motoboyId, long expectedVersion, IReadOnlyList<int> pedidoIdsOrdenados);
        Task<MotoboyQueueDto> CompleteCurrentAsync(Guid estabelecimentoId, int actorUserId, int motoboyId);
        Task<MotoboyQueueDto> ResumeAsync(Guid estabelecimentoId, int actorUserId, int motoboyId);
        Task<MotoboyQueueDto> CancelAsync(Guid estabelecimentoId, int actorUserId, int pedidoId, string? motivo);
        Task<MotoboyQueueDto> GetQueueAsync(Guid estabelecimentoId, int motoboyId);
        Task<LockPedidosResultDto> LockAsync(Guid estabelecimentoId, int actorUserId, IReadOnlyList<int> pedidoIds);
        Task<LockPedidosResultDto> UnlockAsync(Guid estabelecimentoId, int actorUserId, IReadOnlyList<int> pedidoIds);
        Task<MotoboyQueueDto> ArriveAtStoreAsync(Guid estabelecimentoId, int motoboyId);

        // Motoboy
        Task<MotoboyQueueDto> MarkPickedUpAsync(Guid estabelecimentoId, int motoboyId);
        Task<MotoboyQueueDto> MarkArrivedAsync(Guid estabelecimentoId, int motoboyId);
        Task<MotoboyQueueDto> DeliverCurrentAsync(Guid estabelecimentoId, int motoboyId, string? codigo);
        Task<MotoboyQueueDto> FailCurrentAsync(Guid estabelecimentoId, int motoboyId, string? motivo);
        Task<MotoboyQueueDto> RefuseAsync(Guid estabelecimentoId, int motoboyId, int pedidoId, string? motivo);
        Task<MotoboyQueueDto> ReorderByMotoboyAsync(Guid estabelecimentoId, int motoboyId, long expectedVersion, IReadOnlyList<int> pedidoIdsOrdenados);
        Task<MotoboyQueueDto> ResumeByMotoboyAsync(Guid estabelecimentoId, int motoboyId);

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
        Task<CreatedPedidoDto> CreatePedidoAsync(Guid estabelecimentoId, int actorUserId, CreatePedidoRequest request);
        Task<CreatedPedidoDto> UpdatePedidoForSimulatorAsync(Guid estabelecimentoId, int actorUserId, int pedidoId, SimulatorPedidoRequest request);

        // Parametros
        Task<DeliverySettingsDto> GetSettingsAsync(Guid estabelecimentoId);
        Task<DeliverySettingsDto> UpdateSettingsAsync(Guid estabelecimentoId, int actorUserId, UpdateDeliverySettingsRequest request);
    }
}
