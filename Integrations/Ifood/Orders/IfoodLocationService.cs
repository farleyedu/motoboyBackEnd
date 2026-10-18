using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using APIBack.Hubs;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Integrations.Ifood.Orders;

public sealed class IfoodLocationRequest
{
    public long ExpectedRevision { get; set; }
    [Required, StringLength(200)] public string Street { get; set; } = "";
    [Required, StringLength(30)] public string Number { get; set; } = "";
    [Required, StringLength(100)] public string District { get; set; } = "";
    [Required, StringLength(100)] public string City { get; set; } = "";
    [Required, RegularExpression("^[A-Z]{2}$")] public string State { get; set; } = "";
    [RegularExpression("^([0-9]{8})?$")] public string? PostalCode { get; set; }
    [StringLength(150)] public string? Complement { get; set; }
    [StringLength(250)] public string? Reference { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    [Required, StringLength(500, MinimumLength = 3)] public string Note { get; set; } = "";
    public bool PointConfirmed { get; set; }
}

public sealed class IfoodLocationService(NpgsqlDataSource source)
{
    private sealed class Context
    {
        public Guid MerchantId { get; set; }
        public Guid ExternalOrderId { get; set; }
        public string Payload { get; set; } = "{}";
        public string SynchronizationState { get; set; } = "";
        public long Revision { get; set; }
        public string? BlockedCode { get; set; }
    }

    private static async Task<Context> Read(NpgsqlConnection c, NpgsqlTransaction? tx, Guid store, int pedido, bool locked, CancellationToken ct)
    {
        // Mesma ordem do projetor: pedido antes do snapshot.
        if (locked) await c.ExecuteAsync(new CommandDefinition("SELECT id FROM pedido WHERE id=@Pedido AND id_estabelecimento=@Store FOR UPDATE", new { Store = store, Pedido = pedido }, tx, cancellationToken: ct));
        var row = await c.QuerySingleOrDefaultAsync<Context>(new CommandDefinition("""
SELECT l.merchant_id AS MerchantId,l.external_order_id AS ExternalOrderId,s.payload::text AS Payload,
s.revision AS Revision,s.synchronization_state AS SynchronizationState,
CASE
 WHEN s.order_type<>'DELIVERY' OR s.delivered_by IS DISTINCT FROM 'MERCHANT' THEN 'IFOOD_DELIVERY_NOT_APPLICABLE'
 WHEN s.remote_status IN('CANCELLED','CONCLUDED') THEN 'IFOOD_ORDER_TERMINAL'
 WHEN NOT s.data_is_current THEN 'IFOOD_DELIVERY_CONTEXT_UNAVAILABLE'
 WHEN p.motoboy_responsavel IS NOT NULL OR EXISTS(SELECT 1 FROM delivery_route_stops r WHERE r.pedido_id=p.id AND r.estabelecimento_id=@Store AND r.stop_status IN('assigned','en_route')) THEN 'IFOOD_REFRESH_ACTIVE_ROUTE'
 WHEN EXISTS(SELECT 1 FROM ifood_outbound_commands o WHERE o.merchant_id=l.merchant_id AND o.external_order_id=l.external_order_id AND o.estabelecimento_id=@Store AND o.state IN('queued','processing','uncertain','review')) THEN 'IFOOD_COMMAND_ACTIVE_OPERATION'
 WHEN EXISTS(SELECT 1 FROM ifood_delivery_completions d WHERE d.pedido_id=p.id AND d.estabelecimento_id=@Store AND d.state IN('queued','processing','uncertain','verified','review')) THEN 'IFOOD_COMPLETION_ACTIVE_OPERATION'
 WHEN EXISTS(SELECT 1 FROM ifood_events_inbox i WHERE i.merchant_id=l.merchant_id AND i.external_order_id=l.external_order_id AND i.estabelecimento_id=@Store AND i.state IN('queued','processing')) THEN 'IFOOD_LOCATION_EVENT_PENDING'
END AS BlockedCode
FROM ifood_order_links l JOIN ifood_order_snapshots s USING(merchant_id,external_order_id,estabelecimento_id)
JOIN ifood_connections c USING(merchant_id,estabelecimento_id)
JOIN pedido p ON p.id=l.pedido_id AND p.id_estabelecimento=l.estabelecimento_id
WHERE l.estabelecimento_id=@Store AND l.pedido_id=@Pedido AND c.status='authorized' AND c.authorized_at_utc IS NOT NULL AND c.revoked_at_utc IS NULL
""" + (locked ? " FOR UPDATE OF s FOR SHARE OF c" : ""), new { Store = store, Pedido = pedido }, tx, cancellationToken: ct));
        return row ?? throw new DeliveryDomainException(404, "IFOOD_ORDER_NOT_FOUND", "Pedido não encontrado ou conexão revogada.");
    }

    public async Task<object> GetAsync(Guid store, int pedido, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var row = await Read(c, null, store, pedido, false, ct);
        var order = IfoodOrderNormalizer.Normalize(row.Payload, row.MerchantId, row.ExternalOrderId);
        var local = JsonNode.Parse(row.Payload)?[IfoodDeliveryLocation.Key];
        var storePoint = await c.QuerySingleAsync<(string? Latitude, string? Longitude)>(new CommandDefinition(
            "SELECT to_jsonb(e)->>'latitude' AS Latitude,to_jsonb(e)->>'longitude' AS Longitude FROM estabelecimentos e WHERE id=@Store",
            new { Store = store }, cancellationToken: ct));
        var hasLatitude = double.TryParse(storePoint.Latitude, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat);
        var hasLongitude = double.TryParse(storePoint.Longitude, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon);
        var hasStorePoint = hasLatitude && hasLongitude && IfoodDeliveryLocation.ValidPoint(lat, lon);
        return new { row.Revision, row.BlockedCode, address = order.Address, originalAddress = order.OriginalAddress,
            storeCoordinates = hasStorePoint ? new { latitude = lat, longitude = lon } : null,
            source = order.LocationSource, updatedAtUtc = order.LocationUpdatedAtUtc,
            actorUserId = local?["actorUserId"]?.GetValue<int?>(), note = local?["note"]?.GetValue<string>() };
    }

    public async Task SaveAsync(Guid store, int user, int pedido, IfoodLocationRequest request, CancellationToken ct)
    {
        Validator.ValidateObject(request, new ValidationContext(request), true);
        if (!request.PointConfirmed || request.Note.Trim().Length < 3 || !IfoodDeliveryLocation.ValidPoint(request.Latitude, request.Longitude) ||
            new[] { request.Street, request.Number, request.District, request.City, request.Note }.Any(string.IsNullOrWhiteSpace))
            throw new DeliveryDomainException(400, "IFOOD_LOCATION_INVALID", "Preencha o endereço, informe o motivo e confirme o ponto de entrega.");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var row = await Read(c, tx, store, pedido, true, ct);
        if (row.Revision != request.ExpectedRevision) throw new DeliveryDomainException(409, "IFOOD_COMMAND_STALE_REVISION", "O pedido mudou. Reabra o ajuste para conferir os dados atuais.");
        if (row.BlockedCode != null) throw new DeliveryDomainException(409, row.BlockedCode, "O ajuste está bloqueado. Atualize o pedido e confira as operações em andamento.");
        var previous = IfoodOrderNormalizer.Normalize(row.Payload, row.MerchantId, row.ExternalOrderId);
        var address = new JsonObject { ["streetName"] = request.Street.Trim(), ["streetNumber"] = request.Number.Trim(),
            ["neighborhood"] = request.District.Trim(), ["city"] = request.City.Trim(), ["state"] = request.State,
            ["postalCode"] = request.PostalCode, ["complement"] = request.Complement?.Trim(), ["reference"] = request.Reference?.Trim(),
            ["formattedAddress"] = string.Join(", ", new[] { request.Street.Trim(), request.Number.Trim(), request.District.Trim(), request.City.Trim(), request.State }),
            ["coordinates"] = new JsonObject { ["latitude"] = request.Latitude, ["longitude"] = request.Longitude } };
        var payload = IfoodDeliveryLocation.Set(row.Payload, address, "manual", user, request.Note.Trim());
        // Só resolve revisão causada exclusivamente pela localização; eventos e finanças permanecem bloqueados.
        var resolveLocation = row.SynchronizationState == "review" && !previous.ImportedByRecovery &&
            previous.ReviewReasons.Count > 0 && previous.ReviewReasons.All(reason => reason == "IFOOD_DELIVERY_LOCATION_REQUIRED");
        await c.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_order_snapshots SET payload=CAST(@Payload AS jsonb),revision=revision+1,updated_at_utc=NOW(),
synchronization_state=CASE WHEN @Resolve AND NOT EXISTS(SELECT 1 FROM ifood_events_inbox i WHERE i.merchant_id=@MerchantId AND i.external_order_id=@ExternalOrderId AND i.estabelecimento_id=@Store AND i.state IN('queued','processing','review','failed')) THEN 'synced' ELSE synchronization_state END
WHERE merchant_id=@MerchantId AND external_order_id=@ExternalOrderId AND estabelecimento_id=@Store;
UPDATE pedido SET endereco_entrega=@Address,entrega_rua=@Street,entrega_numero=@Number,entrega_bairro=@District,region=@District,
entrega_cidade=@City,entrega_estado=@State,entrega_cep=@PostalCode,latitude=@Latitude,longitude=@Longitude WHERE id=@Pedido AND id_estabelecimento=@Store;
INSERT INTO ifood_reconciliation_audit(merchant_id,estabelecimento_id,external_order_id,action,actor_user_id,note,previous_state,details)
VALUES(@MerchantId,@Store,@ExternalOrderId,'adjust_location',@User,@Note,@PreviousState,CAST(@Audit AS jsonb));
INSERT INTO delivery_realtime_outbox(event_id,event_name,target_group,estabelecimento_id,aggregate_version,payload,occurred_at_utc)
VALUES(@Event,@Name,@Group,@Store,@Revision,CAST(@EventPayload AS jsonb),NOW());
""", new { row.MerchantId, row.ExternalOrderId, Store = store, Pedido = pedido, User = user, Payload = payload,
            Resolve = resolveLocation, PreviousState = row.SynchronizationState,
            Note = request.Note.Trim(), Audit = JsonSerializer.Serialize(new { previousAddress = previous.Address, address, actorUserId = user }),
            Address = address["formattedAddress"]!.ToString(), Street = request.Street.Trim(), Number = request.Number.Trim(), District = request.District.Trim(), City = request.City.Trim(), request.State, request.PostalCode, request.Latitude, request.Longitude,
            Event = Guid.NewGuid(), Name = DeliveryRealtimeEvents.DeliveryOrderUpdated, Group = DeliveryRealtimeEvents.EstablishmentGroup(store), Revision = row.Revision + 1,
            EventPayload = JsonSerializer.Serialize(new { schemaVersion = 2, pedidoId = pedido, estabelecimentoId = store, revision = row.Revision + 1, action = "ifood_location_adjusted" }) }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }
}
