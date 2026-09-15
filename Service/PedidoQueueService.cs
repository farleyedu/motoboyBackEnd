using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;

namespace APIBack.Service
{
    public sealed class PedidoQueueService : IPedidoQueueService
    {
        private readonly IPedidoQueueRepository _repository;

        public PedidoQueueService(IPedidoQueueRepository repository)
        {
            _repository = repository;
        }

        public Task<MotoboyQueueDto> AssignAsync(Guid estabelecimentoId, int actorUserId, int motoboyId, int pedidoId)
        {
            if (motoboyId <= 0)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "motoboyId invalido.");
            }
            if (pedidoId <= 0)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "pedidoId invalido.");
            }
            return _repository.AssignAsync(estabelecimentoId, actorUserId, motoboyId, pedidoId);
        }

        public Task<MotoboyQueueDto> RemoveAsync(Guid estabelecimentoId, int actorUserId, int pedidoId)
        {
            if (pedidoId <= 0)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "pedidoId invalido.");
            }
            return _repository.RemoveAsync(estabelecimentoId, actorUserId, pedidoId);
        }

        public Task<MotoboyQueueDto> ReorderAsync(Guid estabelecimentoId, int actorUserId, int motoboyId, long expectedVersion, IReadOnlyList<int> pedidoIdsOrdenados)
        {
            if (motoboyId <= 0)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "motoboyId invalido.");
            }
            if (pedidoIdsOrdenados == null || pedidoIdsOrdenados.Count == 0)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "pedidoIdsOrdenados nao pode ser vazio.");
            }
            if (pedidoIdsOrdenados.Distinct().Count() != pedidoIdsOrdenados.Count)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "pedidoIdsOrdenados contem duplicados.");
            }
            return _repository.ReorderAsync(estabelecimentoId, actorUserId, motoboyId, expectedVersion, pedidoIdsOrdenados);
        }

        public Task<MotoboyQueueDto> CompleteCurrentAsync(Guid estabelecimentoId, int actorUserId, int motoboyId)
        {
            if (motoboyId <= 0)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "motoboyId invalido.");
            }
            return _repository.CompleteCurrentAsync(estabelecimentoId, actorUserId, motoboyId);
        }

        public Task<MotoboyQueueDto> ResumeAsync(Guid estabelecimentoId, int actorUserId, int motoboyId)
        {
            if (motoboyId <= 0)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "motoboyId invalido.");
            }
            return _repository.ResumeAsync(estabelecimentoId, actorUserId, motoboyId);
        }

        public Task<MotoboyQueueDto> CancelAsync(Guid estabelecimentoId, int actorUserId, int pedidoId, string? motivo)
        {
            if (pedidoId <= 0)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "pedidoId invalido.");
            }
            return _repository.CancelAsync(estabelecimentoId, actorUserId, pedidoId, motivo);
        }

        public Task<MotoboyQueueDto> GetQueueAsync(Guid estabelecimentoId, int motoboyId)
        {
            if (motoboyId <= 0)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "motoboyId invalido.");
            }
            return _repository.GetQueueAsync(estabelecimentoId, motoboyId);
        }
    }
}
