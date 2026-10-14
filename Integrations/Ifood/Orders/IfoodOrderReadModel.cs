using APIBack.DTOs.Delivery;
using Dapper;
using Npgsql;

namespace APIBack.Integrations.Ifood.Orders;

public static class IfoodOrderReadModel
{
    public static async Task<IReadOnlyDictionary<int, IfoodPedidoIdentityDto>> IdentitiesAsync(NpgsqlConnection connection, Guid store, int[] orders, NpgsqlTransaction? transaction = null)
    {
        if (orders.Length == 0 || !await connection.ExecuteScalarAsync<bool>("SELECT to_regclass('ifood_order_links') IS NOT NULL AND to_regclass('ifood_order_snapshots') IS NOT NULL", transaction: transaction))
            return new Dictionary<int, IfoodPedidoIdentityDto>();
        var rows = await connection.QueryAsync<Row>("""
SELECT l.pedido_id AS PedidoId,l.merchant_id AS MerchantId,l.external_order_id AS ExternalOrderId,l.display_number AS DisplayNumber,
s.order_type AS OrderType,s.delivered_by AS DeliveredBy,s.order_amount AS OrderAmount,s.prepaid_amount AS PrepaidAmount,s.pending_amount AS PendingAmount,
s.revision AS Revision,s.remote_status AS RemoteStatus,s.preparation_state AS PreparationState,s.synchronization_state AS SynchronizationState,
to_jsonb(s)->>'cancellation_state' AS CancellationState,to_jsonb(s)->>'delivery_state' AS DeliveryState,
COALESCE((to_jsonb(s)->>'data_is_current')::boolean,TRUE) AS DataIsCurrent
,(CASE WHEN COALESCE((to_jsonb(s)->>'data_is_current')::boolean,TRUE) THEN s.payload->'total'->>'subTotal' END)::numeric AS Subtotal
,(CASE WHEN COALESCE((to_jsonb(s)->>'data_is_current')::boolean,TRUE) THEN s.payload->'total'->>'deliveryFee' END)::numeric AS DeliveryFee
,jsonb_array_length(s.payload->'items') AS ItemCount
FROM ifood_order_links l JOIN ifood_order_snapshots s USING(merchant_id,external_order_id,estabelecimento_id)
WHERE l.estabelecimento_id=@Store AND l.pedido_id=ANY(@Orders)
""", new { Store = store, Orders = orders }, transaction);
        var identities = rows.ToDictionary(r => r.PedidoId, r => new IfoodPedidoIdentityDto
        {
            MerchantId = r.MerchantId, ExternalOrderId = r.ExternalOrderId, DisplayNumber = r.DisplayNumber,
            OrderType = r.OrderType, DeliveredBy = r.DeliveredBy, OrderAmount = r.OrderAmount,
            Subtotal = r.Subtotal, DeliveryFee = r.DeliveryFee, ItemCount = r.ItemCount,
            PrepaidAmount = r.PrepaidAmount, PendingAmount = r.PendingAmount, Revision = r.Revision, RemoteStatus = r.RemoteStatus,
            PreparationState = r.PreparationState, SynchronizationState = r.SynchronizationState,
            CancellationState = r.CancellationState, DeliveryState = r.DeliveryState, DataIsCurrent = r.DataIsCurrent
        });
        if (await connection.ExecuteScalarAsync<bool>("SELECT to_regclass('ifood_command_attempts') IS NOT NULL", transaction: transaction))
        {
            var commands = await connection.QueryAsync<IfoodCommandDto>("""
SELECT l.pedido_id AS PedidoId,o.operation_id AS OperationId,o.command_type AS Command,o.state AS State,
o.expected_order_revision AS ExpectedRevision,o.attempts AS Attempts,o.created_at_utc AS CreatedAtUtc,o.not_before_utc AS NotBeforeUtc,
o.deadline_at_utc AS DeadlineAtUtc,o.send_started_at_utc AS SendStartedAtUtc,o.http_accepted_at_utc AS HttpAcceptedAtUtc,
o.finished_at_utc AS FinishedAtUtc,o.confirmed_event_id AS ConfirmedEventId,o.last_error_code AS LastErrorCode
FROM ifood_outbound_commands o JOIN ifood_order_links l USING(merchant_id,external_order_id,estabelecimento_id)
WHERE o.estabelecimento_id=@Store AND l.pedido_id=ANY(@Orders)
AND o.command_type IN('confirm','start_preparation','ready_to_pickup','dispatch','request_cancellation') AND o.state IN('queued','processing','uncertain','review')
""", new { Store=store,Orders=orders }, transaction);
            foreach(var command in commands)
                if(identities.TryGetValue(command.PedidoId,out var identity))
                {
                    identity.ActiveCommand=command;
                    if(identity.SynchronizationState=="synced")
                        identity.SynchronizationState=command.State switch { "queued" or "processing"=>"pending","uncertain"=>"uncertain",_=>"review" };
                }
        }
        if (await connection.ExecuteScalarAsync<bool>("SELECT to_regprocedure('ifood_delivery_blocked_code(uuid,integer,boolean)') IS NOT NULL", transaction: transaction))
        {
            foreach (var (pedido, identity) in identities)
            {
                identity.DeliveryBlockedCode = await connection.ExecuteScalarAsync<string?>("SELECT ifood_delivery_blocked_code(@Store,@Pedido,FALSE)", new { Store = store, Pedido = pedido }, transaction);
                identity.DepartureBlockedCode = await connection.ExecuteScalarAsync<string?>("SELECT ifood_delivery_blocked_code(@Store,@Pedido,TRUE)", new { Store = store, Pedido = pedido }, transaction);
                identity.CanAssignDelivery = identity.DeliveryBlockedCode == null;
                identity.CanDepartDelivery = identity.DepartureBlockedCode == null;
            }
        }
        else foreach (var identity in identities.Values) identity.DeliveryBlockedCode = identity.DepartureBlockedCode = "IFOOD_DELIVERY_DISABLED";
        return identities;
    }
    public static async Task<IfoodImportedOrder?> DetailsAsync(NpgsqlConnection connection, Guid store, int pedido, bool collectionAllowed=true, NpgsqlTransaction? transaction = null)
    {
        if (!await connection.ExecuteScalarAsync<bool>("SELECT to_regclass('ifood_order_items') IS NOT NULL", transaction: transaction)) return null;
        var row = await connection.QuerySingleOrDefaultAsync<Detail>("""
SELECT l.merchant_id AS MerchantId,l.external_order_id AS ExternalOrderId,s.payload::text AS Payload,
COALESCE((to_jsonb(s)->>'data_is_current')::boolean,TRUE) AS DataIsCurrent,
(s.synchronization_state='synced' AND COALESCE(s.remote_status,'') NOT IN('CANCELLED','CONCLUDED')) AS CollectionAllowed
FROM ifood_order_links l JOIN ifood_order_snapshots s USING(merchant_id,external_order_id,estabelecimento_id)
WHERE l.estabelecimento_id=@Store AND l.pedido_id=@Pedido
""", new { Store = store, Pedido = pedido }, transaction);
        return row == null || !row.DataIsCurrent ? null : IfoodOrderNormalizer.Normalize(row.Payload, row.MerchantId, row.ExternalOrderId, row.CollectionAllowed && collectionAllowed);
    }
    private sealed class Row : IfoodPedidoIdentityBase { public int PedidoId { get; set; } }
    public static async Task<IReadOnlyList<IfoodDisputeSummaryDto>> DisputesAsync(NpgsqlConnection connection, Guid store, int pedido, NpgsqlTransaction? transaction = null)
    {
        if (!await connection.ExecuteScalarAsync<bool>("SELECT to_regclass('ifood_disputes') IS NOT NULL AND to_regclass('ifood_order_links') IS NOT NULL", transaction: transaction))
            return Array.Empty<IfoodDisputeSummaryDto>();
        return (await connection.QueryAsync<IfoodDisputeSummaryDto>("""
SELECT d.dispute_id AS DisputeId,d.state AS State,d.expires_at_utc AS ExpiresAtUtc,d.timeout_action AS TimeoutAction,
d.payload->>'action' AS Action,d.payload->>'handshakeType' AS HandshakeType,d.settlement->>'status' AS SettlementStatus
,COALESCE((to_jsonb(d)->>'revision')::bigint,1) AS Revision,d.response_operation_id AS ResponseOperationId
FROM ifood_disputes d JOIN ifood_order_links l USING(merchant_id,external_order_id,estabelecimento_id)
WHERE d.estabelecimento_id=@Store AND l.pedido_id=@Pedido ORDER BY d.expires_at_utc,d.dispute_id
""",new { Store=store,Pedido=pedido }, transaction)).ToArray();
    }
    private class IfoodPedidoIdentityBase
    {
        public Guid MerchantId { get; set; } public Guid ExternalOrderId { get; set; }
        public string? DisplayNumber { get; set; } public string OrderType { get; set; } = "";
        public string? DeliveredBy { get; set; } public decimal? OrderAmount { get; set; }
        public decimal? Subtotal { get; set; } public decimal? DeliveryFee { get; set; } public int ItemCount { get; set; }
        public decimal? PrepaidAmount { get; set; } public decimal? PendingAmount { get; set; }
        public long Revision { get; set; } public string? RemoteStatus { get; set; } public string? PreparationState { get; set; }
        public string? SynchronizationState { get; set; } public string? CancellationState { get; set; } public string? DeliveryState { get; set; }
        public bool DataIsCurrent { get; set; }
    }
    private sealed class Detail { public Guid MerchantId { get; set; } public Guid ExternalOrderId { get; set; } public string Payload { get; set; } = "{}";
        public bool DataIsCurrent { get; set; } public bool CollectionAllowed { get; set; } }
}
