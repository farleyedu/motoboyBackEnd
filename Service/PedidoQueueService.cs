using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;

namespace APIBack.Service
{
    /// <summary>
    /// Validacao de entrada dos comandos de fila/transferencia. Regras que dependem
    /// do estado (fila, sessao, politica do estabelecimento) ficam no repositorio,
    /// dentro da transacao.
    /// </summary>
    public sealed class PedidoQueueService : IPedidoQueueService
    {
        private const int DefaultTransferListLimit = 50;
        private static readonly HashSet<string> TransferStatusFilters = new(StringComparer.Ordinal)
        {
            TransferStatuses.PendingApproval,
            TransferStatuses.Completed,
            TransferStatuses.Rejected,
            TransferStatuses.Cancelled
        };

        private readonly IPedidoQueueRepository _repository;

        public PedidoQueueService(IPedidoQueueRepository repository)
        {
            _repository = repository;
        }

        // ---- Atendente ---------------------------------------------------------

        public Task<MotoboyQueueDto> AssignAsync(Guid estabelecimentoId, int actorUserId, int motoboyId, int pedidoId)
        {
            EnsurePositive(motoboyId, "motoboyId");
            EnsurePositive(pedidoId, "pedidoId");
            return _repository.AssignAsync(estabelecimentoId, actorUserId, motoboyId, pedidoId);
        }

        public Task<MotoboyQueueDto> RemoveAsync(Guid estabelecimentoId, int actorUserId, int pedidoId)
        {
            EnsurePositive(pedidoId, "pedidoId");
            return _repository.RemoveAsync(estabelecimentoId, actorUserId, pedidoId);
        }

        public Task<MotoboyQueueDto> ReorderAsync(Guid estabelecimentoId, int actorUserId, int motoboyId, long expectedVersion, IReadOnlyList<int> pedidoIdsOrdenados)
        {
            EnsurePositive(motoboyId, "motoboyId");
            EnsureValidOrder(pedidoIdsOrdenados);
            return _repository.ReorderAsync(estabelecimentoId, actorUserId, motoboyId, expectedVersion, pedidoIdsOrdenados);
        }

        public Task<MotoboyQueueDto> CompleteCurrentAsync(Guid estabelecimentoId, int actorUserId, int motoboyId)
        {
            EnsurePositive(motoboyId, "motoboyId");
            return _repository.CompleteCurrentAsync(estabelecimentoId, actorUserId, motoboyId);
        }

        public Task<MotoboyQueueDto> ResumeAsync(Guid estabelecimentoId, int actorUserId, int motoboyId)
        {
            EnsurePositive(motoboyId, "motoboyId");
            return _repository.ResumeAsync(estabelecimentoId, actorUserId, motoboyId);
        }

        public Task<MotoboyQueueDto> CancelAsync(Guid estabelecimentoId, int actorUserId, int pedidoId, string? motivo)
        {
            EnsurePositive(pedidoId, "pedidoId");
            return _repository.CancelAsync(estabelecimentoId, actorUserId, pedidoId,
                DeliveryRules.NormalizeReason(motivo, required: false));
        }

        public Task<MotoboyQueueDto> GetQueueAsync(Guid estabelecimentoId, int motoboyId)
        {
            EnsurePositive(motoboyId, "motoboyId");
            return _repository.GetQueueAsync(estabelecimentoId, motoboyId);
        }

        // ---- Motoboy -----------------------------------------------------------

        public Task<MotoboyQueueDto> MarkPickedUpAsync(Guid estabelecimentoId, int motoboyId)
        {
            EnsurePositive(motoboyId, "motoboyId");
            return _repository.MarkPickedUpAsync(estabelecimentoId, motoboyId);
        }

        public Task<MotoboyQueueDto> MarkArrivedAsync(Guid estabelecimentoId, int motoboyId)
        {
            EnsurePositive(motoboyId, "motoboyId");
            return _repository.MarkArrivedAsync(estabelecimentoId, motoboyId);
        }

        public Task<MotoboyQueueDto> DeliverCurrentAsync(Guid estabelecimentoId, int motoboyId, string? codigo)
        {
            EnsurePositive(motoboyId, "motoboyId");
            var normalizedCode = string.IsNullOrWhiteSpace(codigo) ? null : codigo.Trim();
            if (normalizedCode?.Length > 64)
            {
                throw new DeliveryDomainException(422, "DELIVERY_CODE_INVALID", "Codigo de entrega invalido.");
            }
            return _repository.DeliverCurrentAsync(estabelecimentoId, motoboyId, normalizedCode);
        }

        public Task<MotoboyQueueDto> FailCurrentAsync(Guid estabelecimentoId, int motoboyId, string? motivo)
        {
            EnsurePositive(motoboyId, "motoboyId");
            // O atendente precisa saber por que o pedido voltou; motivo e obrigatorio.
            var reason = DeliveryRules.NormalizeReason(motivo, required: true)!;
            return _repository.FailCurrentAsync(estabelecimentoId, motoboyId, reason);
        }

        public Task<MotoboyQueueDto> RefuseAsync(Guid estabelecimentoId, int motoboyId, int pedidoId, string? motivo)
        {
            EnsurePositive(motoboyId, "motoboyId");
            EnsurePositive(pedidoId, "pedidoId");
            return _repository.RefuseAsync(estabelecimentoId, motoboyId, pedidoId,
                DeliveryRules.NormalizeReason(motivo, required: false));
        }

        public Task<MotoboyQueueDto> ReorderByMotoboyAsync(Guid estabelecimentoId, int motoboyId, long expectedVersion, IReadOnlyList<int> pedidoIdsOrdenados)
        {
            EnsurePositive(motoboyId, "motoboyId");
            EnsureValidOrder(pedidoIdsOrdenados);
            return _repository.ReorderByMotoboyAsync(estabelecimentoId, motoboyId, expectedVersion, pedidoIdsOrdenados);
        }

        public Task<MotoboyQueueDto> ResumeByMotoboyAsync(Guid estabelecimentoId, int motoboyId)
        {
            EnsurePositive(motoboyId, "motoboyId");
            return _repository.ResumeAsync(estabelecimentoId, 0, motoboyId);
        }

        // ---- Transferencia -----------------------------------------------------

        public Task<IReadOnlyList<TransferTargetDto>> GetTransferTargetsAsync(Guid estabelecimentoId, int motoboyId)
        {
            EnsurePositive(motoboyId, "motoboyId");
            return _repository.GetTransferTargetsAsync(estabelecimentoId, motoboyId);
        }

        public Task<TransferResultDto> RequestTransferByMotoboyAsync(Guid estabelecimentoId, int fromMotoboyId, int actorUserId, int pedidoId, int toMotoboyId, string? motivo)
        {
            EnsurePositive(fromMotoboyId, "motoboyId");
            EnsureTransferTarget(pedidoId, toMotoboyId, fromMotoboyId);
            return _repository.RequestTransferByMotoboyAsync(estabelecimentoId, fromMotoboyId, actorUserId, pedidoId, toMotoboyId,
                DeliveryRules.NormalizeReason(motivo, required: false));
        }

        public Task<TransferResultDto> TransferByOperatorAsync(Guid estabelecimentoId, int operatorUserId, int pedidoId, int toMotoboyId, string? motivo)
        {
            EnsureTransferTarget(pedidoId, toMotoboyId, fromMotoboyId: null);
            return _repository.TransferByOperatorAsync(estabelecimentoId, operatorUserId, pedidoId, toMotoboyId,
                DeliveryRules.NormalizeReason(motivo, required: false));
        }

        public Task<TransferResultDto> ApproveTransferAsync(Guid estabelecimentoId, int operatorUserId, long requestId, string? observacao)
        {
            EnsurePositive(requestId, "transferenciaId");
            return _repository.ApproveTransferAsync(estabelecimentoId, operatorUserId, requestId,
                DeliveryRules.NormalizeReason(observacao, required: false, "observacao"));
        }

        public Task<TransferRequestDto> RejectTransferAsync(Guid estabelecimentoId, int operatorUserId, long requestId, string? observacao)
        {
            EnsurePositive(requestId, "transferenciaId");
            return _repository.RejectTransferAsync(estabelecimentoId, operatorUserId, requestId,
                DeliveryRules.NormalizeReason(observacao, required: false, "observacao"));
        }

        public Task<TransferRequestDto> CancelTransferByMotoboyAsync(Guid estabelecimentoId, int motoboyId, long requestId)
        {
            EnsurePositive(motoboyId, "motoboyId");
            EnsurePositive(requestId, "transferenciaId");
            return _repository.CancelTransferByMotoboyAsync(estabelecimentoId, motoboyId, requestId);
        }

        public Task<IReadOnlyList<TransferRequestDto>> ListTransfersAsync(Guid estabelecimentoId, string? status, int limit)
        {
            var normalized = string.IsNullOrWhiteSpace(status) ? TransferStatuses.PendingApproval : status.Trim().ToLowerInvariant();
            if (normalized == "all")
            {
                normalized = null;
            }
            else if (!TransferStatusFilters.Contains(normalized))
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST",
                    "status invalido. Use pending_approval, completed, rejected, cancelled ou all.");
            }
            return _repository.ListTransfersAsync(estabelecimentoId, normalized, limit <= 0 ? DefaultTransferListLimit : limit);
        }

        public Task<IReadOnlyList<TransferRequestDto>> ListMotoboyTransfersAsync(Guid estabelecimentoId, int motoboyId, int limit)
        {
            EnsurePositive(motoboyId, "motoboyId");
            return _repository.ListMotoboyTransfersAsync(estabelecimentoId, motoboyId, limit <= 0 ? DefaultTransferListLimit : limit);
        }

        // ---- Pedido manual -----------------------------------------------------

        public Task<CreatedPedidoDto> UpdatePedidoForSimulatorAsync(Guid estabelecimentoId, int actorUserId, int pedidoId, SimulatorPedidoRequest request)
        {
            var patch = SimulatorOrderRules.Validate(request);
            if (patch.IsEmpty)
            {
                throw new DeliveryDomainException(422, "INVALID_ORDER", "Nenhum campo para alterar.");
            }
            return _repository.UpdatePedidoForSimulatorAsync(estabelecimentoId, actorUserId, pedidoId, patch);
        }

        public Task<CreatedPedidoDto> CreatePedidoAsync(Guid estabelecimentoId, int actorUserId, CreatePedidoRequest request) =>
            _repository.CreatePedidoAsync(estabelecimentoId, actorUserId, ManualOrderRules.Validate(request));

        // ---- Parametros --------------------------------------------------------

        public Task<DeliverySettingsDto> GetSettingsAsync(Guid estabelecimentoId) =>
            _repository.GetSettingsAsync(estabelecimentoId);

        public Task<DeliverySettingsDto> UpdateSettingsAsync(Guid estabelecimentoId, int actorUserId, UpdateDeliverySettingsRequest request)
        {
            if (request == null)
            {
                throw new DeliveryDomainException(400, "INVALID_REQUEST", "Corpo da requisicao obrigatorio.");
            }
            var policy = request.TransferPolicy?.Trim().ToLowerInvariant();
            if (!TransferPolicies.IsConfigurable(policy))
            {
                throw new DeliveryDomainException(422, "INVALID_TRANSFER_POLICY",
                    $"transferPolicy invalida. Use '{TransferPolicies.Direct}' ou '{TransferPolicies.EstablishmentApproval}'.");
            }
            request.TransferPolicy = policy;
            if (request.OrderWindow != null)
            {
                request.OrderWindow = OrderWindowRules.Validate(request.OrderWindow);
            }
            return _repository.UpsertSettingsAsync(estabelecimentoId, actorUserId, request);
        }

        // ---- Validacoes --------------------------------------------------------

        private static void EnsurePositive(long value, string field)
        {
            if (value <= 0)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", $"{field} invalido.");
            }
        }

        private static void EnsureValidOrder(IReadOnlyList<int>? pedidoIdsOrdenados)
        {
            if (pedidoIdsOrdenados == null || pedidoIdsOrdenados.Count == 0)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "pedidoIdsOrdenados nao pode ser vazio.");
            }
            if (pedidoIdsOrdenados.Distinct().Count() != pedidoIdsOrdenados.Count)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "pedidoIdsOrdenados contem duplicados.");
            }
            if (pedidoIdsOrdenados.Any(id => id <= 0))
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "pedidoIdsOrdenados contem id invalido.");
            }
        }

        private static void EnsureTransferTarget(int pedidoId, int toMotoboyId, int? fromMotoboyId)
        {
            EnsurePositive(pedidoId, "pedidoId");
            EnsurePositive(toMotoboyId, "paraMotoboyId");
            if (fromMotoboyId.HasValue && fromMotoboyId.Value == toMotoboyId)
            {
                throw new DeliveryDomainException(422, "TRANSFER_SAME_MOTOBOY", "Escolha outro motoboy para receber o pedido.");
            }
        }
    }
}
