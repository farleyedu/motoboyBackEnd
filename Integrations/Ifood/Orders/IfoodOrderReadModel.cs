using APIBack.DTOs.Delivery;
using Dapper;
using Npgsql;

namespace APIBack.Integrations.Ifood.Orders;

/// <summary>Identidades e pedidos oficiais de um conjunto de pedidos, lidos juntos (o oficial só existe para dado atual e legível).</summary>
public sealed record IfoodQueueView(IReadOnlyDictionary<int, IfoodPedidoIdentityDto> Identities, IReadOnlyDictionary<int, IfoodImportedOrder> Officials);

public static class IfoodOrderReadModel
{
    public static async Task<IReadOnlyDictionary<int, IfoodPedidoIdentityDto>> IdentitiesAsync(NpgsqlConnection connection, Guid store, int[] orders, NpgsqlTransaction? transaction = null) =>
        (await LoadAsync(connection, store, orders, transaction, includeLinks: false)).Identities;

    /// <summary>
    /// Leitura em lote para a fila: um único passe sobre os snapshots (payload lido e normalizado uma vez por pedido) em vez de
    /// <see cref="IdentitiesAsync"/> mais um <see cref="DetailsAsync"/> por parada.
    /// </summary>
    public static Task<IfoodQueueView> QueueViewsAsync(NpgsqlConnection connection, Guid store, int[] orders, NpgsqlTransaction? transaction = null) =>
        LoadAsync(connection, store, orders, transaction);

    private sealed class BlockedRow { public int Pedido { get; set; } public string? Delivery { get; set; } public string? Departure { get; set; } }

    /// <summary>
    /// Vínculos confirmados dos itens do pedido, lidos NO MESMO comando que o snapshot (mesma visão do banco do payload): produto
    /// excluído não entra (o item volta a ser "sem vínculo"). Só acrescenta exibição; nada daqui toca preço, quantidade ou total.
    /// </summary>
    private const string LinksColumn = """
,(SELECT jsonb_object_agg(v.chave_id,jsonb_build_object('produtoId',p.id,'imagemUrl',p.imagem_url,'categoria',c.nome,'atencao',p.atencao_motoboy))::text
  FROM ifood_produto_vinculo v
  JOIN cardapio_produto p ON p.id=v.produto_id AND p.id_estabelecimento=v.estabelecimento_id AND p.deleted_at IS NULL
  LEFT JOIN cardapio_categoria c ON c.id=p.categoria_id
  WHERE v.estabelecimento_id=l.estabelecimento_id AND v.merchant_id=l.merchant_id AND v.tipo='item' AND v.estado='confirmado'
  AND v.chave_id IN (SELECT i->>'id' FROM jsonb_array_elements(CASE WHEN jsonb_typeof(s.payload->'items')='array' THEN s.payload->'items' ELSE '[]'::jsonb END) i)) AS Links
""";

    private static async Task<IfoodQueueView> LoadAsync(NpgsqlConnection connection, Guid store, int[] orders, NpgsqlTransaction? transaction, bool includeLinks = true)
    {
        var noOfficials = new Dictionary<int, IfoodImportedOrder>();
        if (orders.Length == 0 || !await IfoodSchemaProbe.OrdersAsync(connection, transaction))
            return new IfoodQueueView(new Dictionary<int, IfoodPedidoIdentityDto>(), noOfficials);
        var linksSql = includeLinks && await IfoodSchemaProbe.LinksAsync(connection, transaction) ? LinksColumn : ",NULL::text AS Links";
        var rows = await connection.QueryAsync<Row>($"""
SELECT l.pedido_id AS PedidoId,l.merchant_id AS MerchantId,l.external_order_id AS ExternalOrderId,l.display_number AS DisplayNumber,
s.order_type AS OrderType,s.delivered_by AS DeliveredBy,s.order_amount AS OrderAmount,s.prepaid_amount AS PrepaidAmount,s.pending_amount AS PendingAmount,
s.revision AS Revision,s.remote_status AS RemoteStatus,s.preparation_state AS PreparationState,s.synchronization_state AS SynchronizationState,
to_jsonb(s)->>'cancellation_state' AS CancellationState,to_jsonb(s)->>'delivery_state' AS DeliveryState,
COALESCE((to_jsonb(s)->>'data_is_current')::boolean,TRUE) AS DataIsCurrent,
s.payload::text AS Payload, (to_jsonb(s)->>'status_occurred_at_utc')::timestamptz AS StatusOccurredAtUtc,
l.created_at_utc AS LinkedAtUtc
,(CASE WHEN COALESCE((to_jsonb(s)->>'data_is_current')::boolean,TRUE) AND s.payload->'total'->>'subTotal' ~ '^-?[0-9]+(\.[0-9]+)?$' THEN s.payload->'total'->>'subTotal' END)::numeric AS Subtotal
,(CASE WHEN COALESCE((to_jsonb(s)->>'data_is_current')::boolean,TRUE) AND s.payload->'total'->>'deliveryFee' ~ '^-?[0-9]+(\.[0-9]+)?$' THEN s.payload->'total'->>'deliveryFee' END)::numeric AS DeliveryFee
,CASE WHEN jsonb_typeof(s.payload->'items')='array' THEN jsonb_array_length(s.payload->'items') ELSE 0 END AS ItemCount
{linksSql}
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
        var normalized = new Dictionary<int, IfoodImportedOrder>();
        var collectable = new HashSet<int>();
        foreach (var row in rows)
        {
            var identity = identities[row.PedidoId];
            identity.CreatedAtUtc = row.LinkedAtUtc;
            identity.StatusOccurredAtUtc = row.StatusOccurredAtUtc ?? row.LinkedAtUtc;
            IfoodImportedOrder order;
            try { order = IfoodOrderNormalizer.Normalize(row.Payload, row.MerchantId, row.ExternalOrderId); }
            catch (IfoodIntegrationException)
            {
                identity.DataIsCurrent = false; identity.SynchronizationState = "review";
                identity.ReviewReasons = new[] { "IFOOD_ORDER_DATA_REVIEW" }; continue;
            }
            normalized[row.PedidoId] = ApplyLinks(order, row.Links);
            // Mesma regra do detalhe oficial: só cobra no destino enquanto o snapshot está sincronizado e o pedido aberto.
            if (row.SynchronizationState == "synced" && row.RemoteStatus is not ("CANCELLED" or "CONCLUDED")) collectable.Add(row.PedidoId);
            identity.IsTest = order.IsTest;
            identity.CreatedAtUtc = order.CreatedAtUtc;
            identity.StatusOccurredAtUtc = row.StatusOccurredAtUtc ?? row.LinkedAtUtc;
            identity.OrderTiming = order.OrderTiming;
            identity.PreparationStartUtc = order.PreparationStartUtc;
            identity.ScheduledStartUtc = order.ScheduledStartUtc;
            identity.ScheduledEndUtc = order.ScheduledEndUtc;
            identity.ImportedByRecovery = order.ImportedByRecovery;
            identity.ReviewReasons = order.ReviewReasons;
            identity.LocationRequired = order.ReviewReasons.Contains("IFOOD_DELIVERY_LOCATION_REQUIRED");
            identity.LocationSource = order.LocationSource;
            identity.LocationLookupStatus = System.Text.Json.Nodes.JsonNode.Parse(row.Payload)?[IfoodDeliveryLocation.Key]?["lookupStatus"]?.GetValue<string>();
            identity.LocationUpdatedAtUtc = order.LocationUpdatedAtUtc;
        }
        if (await IfoodSchemaProbe.CommandsAsync(connection, transaction))
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
        if (await IfoodSchemaProbe.BlockedCodeAsync(connection, transaction))
        {
            // Uma ida ao banco para todos os pedidos (antes eram duas por pedido).
            var blocked = await connection.QueryAsync<BlockedRow>("""
SELECT p AS Pedido,ifood_delivery_blocked_code(@Store,p,FALSE) AS Delivery,ifood_delivery_blocked_code(@Store,p,TRUE) AS Departure
FROM unnest(@Orders) AS p
""", new { Store = store, Orders = identities.Keys.ToArray() }, transaction);
            foreach (var row in blocked)
            {
                var identity = identities[row.Pedido];
                identity.DeliveryBlockedCode = row.Delivery;
                identity.DepartureBlockedCode = row.Departure;
                identity.CanAssignDelivery = identity.DeliveryBlockedCode == null;
                identity.CanDepartDelivery = identity.DepartureBlockedCode == null;
            }
        }
        else foreach (var identity in identities.Values) identity.DeliveryBlockedCode = identity.DepartureBlockedCode = "IFOOD_DELIVERY_DISABLED";
        if (await IfoodSchemaProbe.CompletionsAsync(connection, transaction))
        {
            var completing = await connection.QueryAsync<int>("SELECT pedido_id FROM ifood_delivery_completions WHERE estabelecimento_id=@Store AND pedido_id=ANY(@Orders) AND state<>'rejected'", new { Store = store, Orders = orders }, transaction);
            foreach (var pedido in completing)
                if (identities.TryGetValue(pedido, out var identity))
                {
                    identity.CanAssignDelivery = identity.CanDepartDelivery = false;
                    identity.DeliveryBlockedCode = identity.DepartureBlockedCode = "IFOOD_COMPLETION_ACTIVE_OPERATION";
                }
        }
        var officials = new Dictionary<int, IfoodImportedOrder>();
        if (normalized.Count > 0 && await IfoodSchemaProbe.ItemsAsync(connection, transaction))
            foreach (var (pedido, order) in normalized)
            {
                var identity = identities[pedido];
                // Dado desatualizado não tem versão oficial (a fila mostra só a identidade e o bloqueio).
                if (identity.DataIsCurrent) officials[pedido] = order.WithCollectionAllowed(collectable.Contains(pedido) && identity.CanAssignDelivery);
            }
        return new IfoodQueueView(identities, officials);
    }
    public static async Task<IfoodImportedOrder?> DetailsAsync(NpgsqlConnection connection, Guid store, int pedido, bool collectionAllowed=true, NpgsqlTransaction? transaction = null)
    {
        if (!await IfoodSchemaProbe.ItemsAsync(connection, transaction)) return null;
        var linksSql = await IfoodSchemaProbe.LinksAsync(connection, transaction) ? LinksColumn : ",NULL::text AS Links";
        var row = await connection.QuerySingleOrDefaultAsync<Detail>($"""
SELECT l.merchant_id AS MerchantId,l.external_order_id AS ExternalOrderId,s.payload::text AS Payload,
COALESCE((to_jsonb(s)->>'data_is_current')::boolean,TRUE) AS DataIsCurrent,
(s.synchronization_state='synced' AND COALESCE(s.remote_status,'') NOT IN('CANCELLED','CONCLUDED')) AS CollectionAllowed
{linksSql}
FROM ifood_order_links l JOIN ifood_order_snapshots s USING(merchant_id,external_order_id,estabelecimento_id)
WHERE l.estabelecimento_id=@Store AND l.pedido_id=@Pedido
""", new { Store = store, Pedido = pedido }, transaction);
        if (row == null || !row.DataIsCurrent) return null;
        // Um snapshot que a normalização atual recusa (itens vazios, valor inválido) não pode derrubar a fila nem o detalhe:
        // IdentitiesAsync já o trata como "em revisão" e quem chama recebe null.
        try { return ApplyLinks(IfoodOrderNormalizer.Normalize(row.Payload, row.MerchantId, row.ExternalOrderId, row.CollectionAllowed && collectionAllowed), row.Links); }
        catch (IfoodIntegrationException) { return null; }
    }

    /// <summary>
    /// Acrescenta aos itens o que vem do produto vinculado (foto de reserva, categoria, aviso de bag). Preço, quantidade, unidade e
    /// total ficam exatamente como no snapshot; item sem vínculo sai como está, e JSON de vínculo ilegível nunca derruba a leitura.
    /// </summary>
    internal static IfoodImportedOrder ApplyLinks(IfoodImportedOrder order, string? linksJson)
    {
        if (string.IsNullOrEmpty(linksJson)) return order;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(linksJson);
            var links = document.RootElement;
            if (links.ValueKind != System.Text.Json.JsonValueKind.Object) return order;
            var items = new List<IfoodImportedItem>(order.Items.Count);
            foreach (var item in order.Items)
            {
                if (item.ExternalItemId == null || !links.TryGetProperty(item.ExternalItemId, out var link) || link.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !link.TryGetProperty("produtoId", out var id) || !id.TryGetGuid(out var product)) { items.Add(item); continue; }
                items.Add(item with
                {
                    ProdutoId = product, Vinculado = true,
                    ImagemUrl = item.ImagemUrl ?? IfoodOrderNormalizer.Text(link, "imagemUrl"),
                    CategoriaNome = IfoodOrderNormalizer.Text(link, "categoria"),
                    AtencaoMotoboy = link.TryGetProperty("atencao", out var attention) && attention.ValueKind == System.Text.Json.JsonValueKind.True
                });
            }
            return order.WithItems(items);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { return order; }
    }

    private sealed class Row : IfoodPedidoIdentityBase { public int PedidoId { get; set; } public string? Links { get; set; } }
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
        public string Payload { get; set; } = "{}";
        public DateTimeOffset? StatusOccurredAtUtc { get; set; }
        public DateTimeOffset LinkedAtUtc { get; set; }
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
        public bool DataIsCurrent { get; set; } public bool CollectionAllowed { get; set; } public string? Links { get; set; } }
}
