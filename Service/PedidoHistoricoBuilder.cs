using System;
using System.Collections.Generic;
using System.Linq;
using APIBack.DTOs.Delivery;

namespace APIBack.Service
{
    /// <summary>Linha de delivery_route_stops: cada tentativa de entrega de um pedido.</summary>
    public sealed class PedidoHistoricoStopRow
    {
        public long Id { get; set; }
        public int MotoboyId { get; set; }
        public string? MotoboyNome { get; set; }
        public string StopStatus { get; set; } = string.Empty;
        public DateTimeOffset AssignedAtUtc { get; set; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? PickedUpAtUtc { get; set; }
        public DateTimeOffset? ArrivedAtUtc { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public DateTimeOffset? FailedAtUtc { get; set; }
        public DateTimeOffset? RefusedAtUtc { get; set; }
        public DateTimeOffset? RemovedAtUtc { get; set; }
        public DateTimeOffset? CanceledAtUtc { get; set; }
        public DateTimeOffset? TransferredAtUtc { get; set; }
        public long? TransferRequestId { get; set; }
        public string? FailureReason { get; set; }
        public string? RefusalReason { get; set; }
        public string? CancelReason { get; set; }
        public string? CompletedBy { get; set; }
    }

    /// <summary>Linha de delivery_transfer_requests.</summary>
    public sealed class PedidoHistoricoTransferRow
    {
        public long Id { get; set; }
        public int FromMotoboyId { get; set; }
        public string? FromMotoboyNome { get; set; }
        public int ToMotoboyId { get; set; }
        public string? ToMotoboyNome { get; set; }
        public string Status { get; set; } = string.Empty;
        public string RequestedBy { get; set; } = string.Empty;
        public string? Reason { get; set; }
        public DateTimeOffset RequestedAtUtc { get; set; }
        public DateTimeOffset? DecidedAtUtc { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public string? DecisionNote { get; set; }
    }

    /// <summary>
    /// Monta a linha do tempo do pedido a partir do que o backend ja grava: as paradas
    /// da fila (uma por tentativa, com todos os marcos) e as transferencias. Nao ha
    /// tabela de eventos: gravar o mesmo fato em dois lugares abriria espaco para
    /// divergirem.
    /// </summary>
    public static class PedidoHistoricoBuilder
    {
        // Desempate para marcos gravados no mesmo instante (ex.: atribuir ja inicia a entrega).
        private static readonly string[] TypeOrder =
        {
            "criado", "atribuido", "transferencia_solicitada", "transferencia_rejeitada", "transferencia_concluida",
            "entrega_iniciada", "coletado", "chegou", "entregue", "nao_entregue", "recusado",
            "removido_da_fila", "cancelado",
        };

        public static PedidoHistoricoDto Build(
            int pedidoId,
            DateTimeOffset? createdAtUtc,
            IEnumerable<PedidoHistoricoStopRow> stops,
            IEnumerable<PedidoHistoricoTransferRow> transfers)
        {
            var events = new List<PedidoHistoricoEventoDto>();

            if (createdAtUtc.HasValue)
            {
                events.Add(new PedidoHistoricoEventoDto
                {
                    Tipo = "criado",
                    OcorridoEmUtc = createdAtUtc.Value,
                    Ator = "sistema",
                });
            }

            foreach (var stop in stops)
            {
                void Add(string tipo, DateTimeOffset? at, string? motivo = null, string? ator = null)
                {
                    if (!at.HasValue) return;
                    events.Add(new PedidoHistoricoEventoDto
                    {
                        Tipo = tipo,
                        OcorridoEmUtc = at.Value,
                        MotoboyId = stop.MotoboyId,
                        MotoboyNome = stop.MotoboyNome,
                        Motivo = string.IsNullOrWhiteSpace(motivo) ? null : motivo.Trim(),
                        Ator = ator,
                    });
                }

                Add("atribuido", stop.AssignedAtUtc, ator: "atendente");
                Add("entrega_iniciada", stop.StartedAtUtc, ator: "sistema");
                Add("coletado", stop.PickedUpAtUtc, ator: "motoboy");
                Add("chegou", stop.ArrivedAtUtc, ator: "motoboy");
                Add("entregue", stop.CompletedAtUtc, ator: stop.CompletedBy == "operator" ? "atendente" : "motoboy");
                Add("nao_entregue", stop.FailedAtUtc, stop.FailureReason, "motoboy");
                Add("recusado", stop.RefusedAtUtc, stop.RefusalReason, "motoboy");
                Add("cancelado", stop.CanceledAtUtc, stop.CancelReason, "atendente");

                // A saida por transferencia ja aparece nos eventos de transferencia.
                if (stop.TransferredAtUtc is null && stop.TransferRequestId is null)
                {
                    Add("removido_da_fila", stop.RemovedAtUtc, ator: "atendente");
                }
            }

            foreach (var transfer in transfers)
            {
                var detail = $"De {transfer.FromMotoboyNome ?? $"motoboy {transfer.FromMotoboyId}"} "
                    + $"para {transfer.ToMotoboyNome ?? $"motoboy {transfer.ToMotoboyId}"}";
                var requestedBy = transfer.RequestedBy == "operator" ? "atendente" : "motoboy";

                events.Add(new PedidoHistoricoEventoDto
                {
                    Tipo = "transferencia_solicitada",
                    OcorridoEmUtc = transfer.RequestedAtUtc,
                    MotoboyId = transfer.FromMotoboyId,
                    MotoboyNome = transfer.FromMotoboyNome,
                    Motivo = string.IsNullOrWhiteSpace(transfer.Reason) ? null : transfer.Reason.Trim(),
                    Ator = requestedBy,
                    Detalhe = detail,
                });

                if (transfer.Status == "rejected" && transfer.DecidedAtUtc.HasValue)
                {
                    events.Add(new PedidoHistoricoEventoDto
                    {
                        Tipo = "transferencia_rejeitada",
                        OcorridoEmUtc = transfer.DecidedAtUtc.Value,
                        MotoboyId = transfer.FromMotoboyId,
                        MotoboyNome = transfer.FromMotoboyNome,
                        Motivo = string.IsNullOrWhiteSpace(transfer.DecisionNote) ? null : transfer.DecisionNote.Trim(),
                        Ator = "atendente",
                        Detalhe = detail,
                    });
                }

                if (transfer.CompletedAtUtc.HasValue)
                {
                    events.Add(new PedidoHistoricoEventoDto
                    {
                        Tipo = "transferencia_concluida",
                        OcorridoEmUtc = transfer.CompletedAtUtc.Value,
                        MotoboyId = transfer.ToMotoboyId,
                        MotoboyNome = transfer.ToMotoboyNome,
                        Ator = requestedBy,
                        Detalhe = detail,
                    });
                }
            }

            return new PedidoHistoricoDto
            {
                PedidoId = pedidoId,
                Eventos = events
                    .OrderBy(item => item.OcorridoEmUtc)
                    .ThenBy(item => Array.IndexOf(TypeOrder, item.Tipo))
                    .ToList(),
            };
        }
    }
}
