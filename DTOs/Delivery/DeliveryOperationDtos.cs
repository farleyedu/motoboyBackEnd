using System;
using System.Collections.Generic;

namespace APIBack.DTOs.Delivery
{
    // ---- Parametros do estabelecimento ------------------------------------------

    public static class TransferPolicies
    {
        /// <summary>O proprio motoboy decide: a transferencia acontece na hora.</summary>
        public const string Direct = "direct";
        /// <summary>O estabelecimento precisa aprovar no painel antes da troca.</summary>
        public const string EstablishmentApproval = "establishment_approval";
        /// <summary>Transferencia feita pelo atendente (sempre direta). So aparece no historico.</summary>
        public const string Operator = "operator";

        public static bool IsConfigurable(string? value) =>
            value == Direct || value == EstablishmentApproval;
    }

    public sealed class DeliveryPoliciesDto
    {
        public string TransferPolicy { get; set; } = TransferPolicies.Direct;
        public bool RequireDeliveryCode { get; set; }
        public bool AllowMotoboyReorder { get; set; } = true;
        public bool AllowMotoboyRefuse { get; set; } = true;
    }

    public sealed class DeliverySettingsDto
    {
        public Guid EstabelecimentoId { get; set; }
        public string TransferPolicy { get; set; } = TransferPolicies.Direct;
        public bool RequireDeliveryCode { get; set; }
        public bool AllowMotoboyReorder { get; set; } = true;
        public bool AllowMotoboyRefuse { get; set; } = true;
        /// <summary>true quando o estabelecimento nunca salvou parametros (valem os padroes).</summary>
        public bool IsDefault { get; set; }
        public DateTimeOffset? UpdatedAtUtc { get; set; }

        public DeliveryPoliciesDto ToPolicies() => new()
        {
            TransferPolicy = TransferPolicy,
            RequireDeliveryCode = RequireDeliveryCode,
            AllowMotoboyReorder = AllowMotoboyReorder,
            AllowMotoboyRefuse = AllowMotoboyRefuse
        };
    }

    public sealed class UpdateDeliverySettingsRequest
    {
        public string? TransferPolicy { get; set; }
        public bool RequireDeliveryCode { get; set; }
        public bool AllowMotoboyReorder { get; set; } = true;
        public bool AllowMotoboyRefuse { get; set; } = true;
    }

    // ---- Acoes do motoboy ------------------------------------------------------

    public sealed class DeliverStopRequest
    {
        /// <summary>Codigo informado pelo cliente; obrigatorio quando o estabelecimento exige.</summary>
        public string? Codigo { get; set; }
    }

    public sealed class FailStopRequest
    {
        public string? Motivo { get; set; }
    }

    public sealed class RefuseStopRequest
    {
        public string? Motivo { get; set; }
    }

    // ---- Transferencia -----------------------------------------------------------

    public static class TransferStatuses
    {
        public const string PendingApproval = "pending_approval";
        public const string Completed = "completed";
        public const string Rejected = "rejected";
        public const string Cancelled = "cancelled";
    }

    public sealed class TransferPedidoRequest
    {
        public int ParaMotoboyId { get; set; }
        public string? Motivo { get; set; }
    }

    public sealed class TransferDecisionRequest
    {
        public string? Observacao { get; set; }
    }

    public sealed class TransferRequestDto
    {
        public long Id { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public int PedidoId { get; set; }
        public int FromMotoboyId { get; set; }
        public string? FromMotoboyNome { get; set; }
        public int ToMotoboyId { get; set; }
        public string? ToMotoboyNome { get; set; }
        public string Status { get; set; } = TransferStatuses.PendingApproval;
        public string Policy { get; set; } = TransferPolicies.Direct;
        public string RequestedBy { get; set; } = "motoboy";
        public string? Reason { get; set; }
        public DateTimeOffset RequestedAtUtc { get; set; }
        public DateTimeOffset? DecidedAtUtc { get; set; }
        public string? DecisionNote { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
    }

    public sealed class TransferResultDto
    {
        public TransferRequestDto Transfer { get; set; } = new();
        /// <summary>Fila do motoboy de origem depois da operacao.</summary>
        public MotoboyQueueDto? SourceQueue { get; set; }
        /// <summary>Fila do motoboy de destino (so quando a transferencia ja foi executada).</summary>
        public MotoboyQueueDto? TargetQueue { get; set; }
    }

    public sealed class TransferTargetDto
    {
        public int MotoboyId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Avatar { get; set; }
        public bool HasCurrentDelivery { get; set; }
        public int QueueSize { get; set; }
    }

    // ---- Pedido manual -------------------------------------------------------------

    public sealed class CreatePedidoRequest
    {
        public string? NomeCliente { get; set; }
        public string? TelefoneCliente { get; set; }
        public string? Rua { get; set; }
        public string? Numero { get; set; }
        public string? Complemento { get; set; }
        public string? Bairro { get; set; }
        public string? Cidade { get; set; }
        public string? Estado { get; set; }
        public string? Cep { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string? Items { get; set; }
        public decimal? Value { get; set; }
        public string? TipoPagamento { get; set; }
        public decimal? Troco { get; set; }
        public string? Observacoes { get; set; }
        /// <summary>Minutos a partir de agora; padrao 40.</summary>
        public int? PrevisaoMinutos { get; set; }
        /// <summary>Codigo que o cliente informa ao motoboy (opcional).</summary>
        public string? CodigoEntrega { get; set; }
    }

    public sealed class CreatedPedidoDto
    {
        public int Id { get; set; }
    }

    public sealed class TransferListResponse
    {
        public List<TransferRequestDto> Items { get; set; } = new();
    }
}
