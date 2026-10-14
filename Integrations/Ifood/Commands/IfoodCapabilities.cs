using System.Data;
using APIBack.DTOs.Delivery;
using APIBack.Integrations.Ifood.Exceptions;
using APIBack.Service;
using Dapper;

namespace APIBack.Integrations.Ifood.Commands;

public static class IfoodCapabilityRules
{
    public static IReadOnlyList<IfoodActionCapabilityDto> Actions(IfoodCommandContext context, IfoodOptions options,
        DateTimeOffset now, bool authorized, bool active)
    {
        return new[] { "confirm", "start_preparation", "ready_to_pickup", "dispatch", "request_cancellation" }
            .Select(command => Evaluate(command)).ToArray();

        IfoodActionCapabilityDto Evaluate(string command)
        {
            try
            {
                if (!authorized) return new(command, false, "IFOOD_MERCHANT_NOT_AUTHORIZED");
                if (active) return new(command, false, "IFOOD_COMMAND_ACTIVE_OPERATION");
                if (command == "request_cancellation")
                {
                    IfoodExceptionRules.Enabled(options, context, command, context.Revision);
                    IfoodExceptionRules.Cancellation(context);
                    return new(command, true, null);
                }
                var window = IfoodCommandRules.Validate(command, context, context.Revision, now, options);
                return new(command, true, null, window.NotBeforeUtc, window.DeadlineAtUtc);
            }
            catch (DeliveryDomainException ex) { return new(command, false, ex.Code); }
        }
    }
}

public sealed partial class IfoodCommandRepository
{
    // Consulta consistente, sem HTTP, reserva, segredo ou efeito colateral. O POST revalida tudo.
    public Task<IfoodCapabilitiesDto> CapabilitiesAsync(Guid store, int pedido, CancellationToken ct = default) => Guard(async () =>
    {
        await using var c = await _source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var context = await c.QuerySingleOrDefaultAsync<IfoodCommandContext>(new CommandDefinition("""
SELECT l.pedido_id AS PedidoId,s.merchant_id AS MerchantId,s.external_order_id AS ExternalOrderId,s.estabelecimento_id AS EstabelecimentoId,
s.revision AS Revision,s.remote_status AS RemoteStatus,s.preparation_state AS PreparationState,s.synchronization_state AS SynchronizationState,
s.cancellation_state AS CancellationState,s.data_is_current AS DataIsCurrent,s.payload::text AS Payload
FROM ifood_order_snapshots s JOIN ifood_order_links l USING(merchant_id,external_order_id,estabelecimento_id)
WHERE l.estabelecimento_id=@Store AND l.pedido_id=@Pedido
""", new { Store = store, Pedido = pedido }, tx, cancellationToken: ct));
        if (context == null) throw new DeliveryDomainException(404, "IFOOD_ORDER_NOT_FOUND", "Pedido iFood não encontrado.");
        var authorized = await c.ExecuteScalarAsync<bool>(new CommandDefinition("""
SELECT EXISTS(SELECT 1 FROM ifood_connections WHERE estabelecimento_id=@EstabelecimentoId AND merchant_id=@MerchantId
AND status='authorized' AND authorized_at_utc IS NOT NULL AND revoked_at_utc IS NULL)
""", context, tx, cancellationToken: ct));
        var operations = (await c.QueryAsync<IfoodCommandDto>(new CommandDefinition("""
SELECT o.operation_id AS OperationId,@PedidoId AS PedidoId,o.command_type AS Command,o.state AS State,
o.expected_order_revision AS ExpectedRevision,o.attempts AS Attempts,o.created_at_utc AS CreatedAtUtc,o.not_before_utc AS NotBeforeUtc,
o.deadline_at_utc AS DeadlineAtUtc,o.send_started_at_utc AS SendStartedAtUtc,o.http_accepted_at_utc AS HttpAcceptedAtUtc,
o.finished_at_utc AS FinishedAtUtc,o.confirmed_event_id AS ConfirmedEventId,o.last_error_code AS LastErrorCode,(to_jsonb(o)->>'target_dispute_id')::uuid AS DisputeId
FROM ifood_outbound_commands o
WHERE o.estabelecimento_id=@EstabelecimentoId AND o.merchant_id=@MerchantId AND o.external_order_id=@ExternalOrderId
ORDER BY (o.state IN('queued','processing','uncertain','review')) DESC,o.created_at_utc DESC,o.operation_id DESC LIMIT 50
""", context, tx, cancellationToken: ct))).ToArray();
        var now = _clock.GetUtcNow();
        var active = await c.ExecuteScalarAsync<bool>(new CommandDefinition("""
SELECT EXISTS(SELECT 1 FROM ifood_outbound_commands WHERE estabelecimento_id=@EstabelecimentoId AND merchant_id=@MerchantId AND external_order_id=@ExternalOrderId
AND command_type IN('confirm','start_preparation','ready_to_pickup','dispatch','request_cancellation') AND state IN('queued','processing','uncertain','review'))
""", context, tx, cancellationToken: ct));
        var result = new IfoodCapabilitiesDto { PedidoId = pedido, Revision = context.Revision, ServerTimeUtc = now,
            Actions = IfoodCapabilityRules.Actions(context, _options, now, authorized, active), Operations = operations };
        await tx.CommitAsync(ct);
        return result;
    });
}
