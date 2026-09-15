using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;

namespace APIBack.Service.Interface
{
    public interface IPedidoQueueService
    {
        Task<MotoboyQueueDto> AssignAsync(Guid estabelecimentoId, int actorUserId, int motoboyId, int pedidoId);
        Task<MotoboyQueueDto> RemoveAsync(Guid estabelecimentoId, int actorUserId, int pedidoId);
        Task<MotoboyQueueDto> ReorderAsync(Guid estabelecimentoId, int actorUserId, int motoboyId, long expectedVersion, IReadOnlyList<int> pedidoIdsOrdenados);
        Task<MotoboyQueueDto> CompleteCurrentAsync(Guid estabelecimentoId, int actorUserId, int motoboyId);
        Task<MotoboyQueueDto> ResumeAsync(Guid estabelecimentoId, int actorUserId, int motoboyId);
        Task<MotoboyQueueDto> CancelAsync(Guid estabelecimentoId, int actorUserId, int pedidoId, string? motivo);
        Task<MotoboyQueueDto> GetQueueAsync(Guid estabelecimentoId, int motoboyId);
    }
}
