using System.Text.Json;
using APIBack.Hubs;
using APIBack.Integrations.Ifood.Events;
using Dapper;
using Npgsql;

namespace APIBack.Integrations.Ifood.Orders;

public sealed record IfoodDetailsReservation(Guid MerchantId, Guid ExternalOrderId, Guid EstabelecimentoId, Guid? LeaseId,
    [property: System.Text.Json.Serialization.JsonIgnore] string? PayloadJson)
{
    public override string ToString() => "[reserva de detalhes iFood; payload omitido]";
}

public sealed class IfoodOrderImportRepository
{
    private readonly NpgsqlDataSource _source;
    private readonly IfoodItemLinkService? _links;
    private readonly ILogger<IfoodOrderImportRepository>? _logger;
    public IfoodOrderImportRepository(NpgsqlDataSource source, IfoodItemLinkService? links = null, ILogger<IfoodOrderImportRepository>? logger = null)
        => (_source, _links, _logger) = (source, links, logger);

    public Task<IfoodDetailsReservation?> ReserveDetailsAsync(IfoodInboxReservation item, int seconds, CancellationToken ct, bool force = false) => Guard(async () =>
    {
        if (item.ExternalOrderId is null || item.ExternalOrderId == Guid.Empty) throw new IfoodIntegrationException("IFOOD_ORDER_MISSING_ID");
        await using var connection = await _source.OpenConnectionAsync(ct);
        await EnsureAuthorizedAsync(connection, null, item.EstabelecimentoId, item.MerchantId, ct);
        var reservation = await connection.QuerySingleOrDefaultAsync<IfoodDetailsReservation>(new CommandDefinition("""
INSERT INTO ifood_order_details_cache(merchant_id,external_order_id,estabelecimento_id,lease_id,lease_until_utc)
VALUES(@MerchantId,@ExternalOrderId,@EstabelecimentoId,@Lease,NOW()+make_interval(secs=>@Seconds))
ON CONFLICT(merchant_id,external_order_id) DO UPDATE SET lease_id=EXCLUDED.lease_id,lease_until_utc=EXCLUDED.lease_until_utc,payload=NULL
WHERE ifood_order_details_cache.estabelecimento_id=EXCLUDED.estabelecimento_id
AND (ifood_order_details_cache.payload IS NULL OR (@Force AND ifood_order_details_cache.fetched_at_utc<NOW()-INTERVAL '15 seconds'))
AND ifood_order_details_cache.next_fetch_at_utc<=NOW()
AND (ifood_order_details_cache.lease_id IS NULL OR ifood_order_details_cache.lease_until_utc<=NOW())
RETURNING merchant_id AS MerchantId,external_order_id AS ExternalOrderId,estabelecimento_id AS EstabelecimentoId,lease_id AS LeaseId,NULL::text AS PayloadJson
""", new { item.MerchantId, item.ExternalOrderId, item.EstabelecimentoId, Lease = Guid.NewGuid(), Seconds = seconds, Force = force }, cancellationToken: ct));
        if (reservation != null) return reservation;
        return await connection.QuerySingleOrDefaultAsync<IfoodDetailsReservation>(new CommandDefinition("""
SELECT merchant_id AS MerchantId,external_order_id AS ExternalOrderId,estabelecimento_id AS EstabelecimentoId,
NULL::uuid AS LeaseId,payload::text AS PayloadJson FROM ifood_order_details_cache
WHERE merchant_id=@MerchantId AND external_order_id=@ExternalOrderId AND estabelecimento_id=@EstabelecimentoId AND payload IS NOT NULL
""", item, cancellationToken: ct));
    });

    public Task RenewDetailsAsync(IfoodDetailsReservation reservation, int seconds, CancellationToken ct) => Guard(async () =>
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        var updated = await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_order_details_cache SET lease_until_utc=NOW()+make_interval(secs=>@Seconds)
WHERE merchant_id=@MerchantId AND external_order_id=@ExternalOrderId AND estabelecimento_id=@EstabelecimentoId
AND lease_id=@LeaseId AND lease_until_utc>NOW() AND payload IS NULL
""", new { reservation.MerchantId, reservation.ExternalOrderId, reservation.EstabelecimentoId, reservation.LeaseId, Seconds = seconds }, cancellationToken: ct));
        if (updated != 1) throw new IfoodIntegrationException("IFOOD_DETAILS_LEASE_LOST", retryable: true);
        return true;
    });

    public Task StoreDetailsAsync(IfoodDetailsReservation reservation, string json, CancellationToken ct) => Guard(async () =>
    {
        var sanitized = IfoodOrderNormalizer.Sanitize(json, reservation.MerchantId, reservation.ExternalOrderId);
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await EnsureAuthorizedAsync(connection, transaction, reservation.EstabelecimentoId, reservation.MerchantId, ct);
        var updated = await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_order_details_cache SET payload=CAST(@Payload AS jsonb),fetched_at_utc=NOW(),lease_id=NULL,lease_until_utc=NULL,last_error_code=NULL
WHERE merchant_id=@MerchantId AND external_order_id=@ExternalOrderId AND estabelecimento_id=@EstabelecimentoId
AND lease_id=@LeaseId AND lease_until_utc>NOW() AND payload IS NULL
""", new { reservation.MerchantId, reservation.ExternalOrderId, reservation.EstabelecimentoId, reservation.LeaseId, Payload = sanitized }, transaction, cancellationToken: ct));
        if (updated != 1) throw new IfoodIntegrationException("IFOOD_DETAILS_LEASE_LOST", retryable: true);
        await transaction.CommitAsync(ct);
        return true;
    });

    public Task DetailsFailedAsync(IfoodDetailsReservation reservation, string code, TimeSpan delay, CancellationToken ct) => Guard(async () =>
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_order_details_cache SET lease_id=NULL,lease_until_utc=NULL,last_error_code=@Code,next_fetch_at_utc=NOW()+make_interval(secs=>@Seconds)
WHERE merchant_id=@MerchantId AND external_order_id=@ExternalOrderId AND estabelecimento_id=@EstabelecimentoId AND lease_id=@LeaseId AND payload IS NULL
""", new { reservation.MerchantId, reservation.ExternalOrderId, reservation.EstabelecimentoId, reservation.LeaseId, Code = code, Seconds = Math.Max(30, delay.TotalSeconds) }, cancellationToken: ct));
        return true;
    });

    public Task<int> ImportAsync(IfoodInboxReservation item, IfoodImportedOrder order, CancellationToken ct = default) => Guard(async () =>
    {
        var recovered = item.EventCode is not ("PLC" or "PLACED");
        if (item.ExternalOrderId != order.ExternalOrderId || item.MerchantId != order.MerchantId)
            throw new IfoodIntegrationException("IFOOD_ORDER_IDENTITY_MISMATCH");
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var owned = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition("""
SELECT id FROM ifood_events_inbox WHERE id=@Id AND merchant_id=@MerchantId AND estabelecimento_id=@EstabelecimentoId
AND external_order_id=@ExternalOrderId AND state='processing' AND lease_id=@LeaseId AND lease_until_utc>clock_timestamp() FOR UPDATE
""", item, transaction, cancellationToken: ct));
        if (owned == null) throw new IfoodIntegrationException("IFOOD_EVENT_LEASE_LOST", retryable: true);
        await EnsureAuthorizedAsync(connection, transaction, item.EstabelecimentoId, item.MerchantId, ct);
        // Serializa efeitos do mesmo pedido, inclusive eventos PLC diferentes, sem segurar lock durante HTTP.
        await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@Key,0))",
            new { Key = $"ifood:{item.MerchantId:D}:{item.ExternalOrderId:D}" }, transaction, cancellationToken: ct));
        var existing = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition("""
SELECT pedido_id FROM ifood_order_links WHERE merchant_id=@MerchantId AND external_order_id=@ExternalOrderId AND estabelecimento_id=@EstabelecimentoId
""", item, transaction, cancellationToken: ct));
        int pedido;
        if (existing.HasValue)
        {
            var complete = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
SELECT EXISTS(SELECT 1 FROM ifood_order_snapshots WHERE merchant_id=@MerchantId AND external_order_id=@ExternalOrderId AND estabelecimento_id=@EstabelecimentoId)
AND EXISTS(SELECT 1 FROM ifood_order_items WHERE merchant_id=@MerchantId AND external_order_id=@ExternalOrderId AND estabelecimento_id=@EstabelecimentoId)
""", item, transaction, cancellationToken: ct));
            if (!complete) throw new IfoodIntegrationException("IFOOD_LEGACY_LINK_REQUIRES_REVIEW");
            pedido = existing.Value;
        }
        else
        {
            var columns = (await connection.QueryAsync<Column>(new CommandDefinition("""
SELECT column_name AS Name,data_type AS Type,character_maximum_length AS MaxLength FROM information_schema.columns
WHERE table_schema=current_schema() AND table_name='pedido'
""", transaction: transaction, cancellationToken: ct))).ToDictionary(c => c.Name);
            if (!columns.ContainsKey("origem_ref") || !columns.ContainsKey("subtotal")) throw new IfoodIntegrationException("IFOOD_SCHEMA_UNAVAILABLE");
            string TextTimeSql(string column, string parameter)
            {
                var max = columns[column].MaxLength;
                // previsao_entrega legada é varchar(5) ("HH:MI"), como o horario_pedido (ver DeliveryRules.LocalNowSql).
                var format = column == "horario_pedido"
                    ? max is null or >= 8 ? "HH24:MI:SS" : max >= 5 ? "HH24:MI" : null
                    : column == "previsao_entrega"
                        ? max is null or >= 19 ? "YYYY-MM-DD HH24:MI:SS" : max >= 5 ? "HH24:MI" : null
                        : max is null or >= 19 ? "YYYY-MM-DD HH24:MI:SS" : max >= 10 ? "YYYY-MM-DD" : null;
                if (format == null) throw new IfoodIntegrationException("IFOOD_ORDER_SCHEMA_UNSUPPORTED");
                return $"to_char({parameter}::timestamptz AT TIME ZONE 'America/Sao_Paulo','{format}')";
            }
            string TimeSql(string column, string parameter = "@CreatedAtUtc") => columns.GetValueOrDefault(column)?.Type switch
            {
                "timestamp with time zone" => $"{parameter}::timestamptz",
                "timestamp without time zone" => $"({parameter}::timestamptz AT TIME ZONE 'America/Sao_Paulo')",
                "date" => $"({parameter}::timestamptz AT TIME ZONE 'America/Sao_Paulo')::date",
                "time without time zone" => $"({parameter}::timestamptz AT TIME ZONE 'America/Sao_Paulo')::time",
                "text" or "character varying" => TextTimeSql(column, parameter),
                _ => throw new IfoodIntegrationException("IFOOD_ORDER_SCHEMA_UNSUPPORTED")
            };
            var address = order.Address;
            string? Text(string key) => IfoodOrderNormalizer.Text(address, key);
            static decimal? LegacyMoney(decimal? money) => money <= 9_999_999_999.99m ? money : null;
            // Prazo prometido pelo iFood: sem ele o painel ficava "Sem SLA" e "Prazo em --" em todo pedido iFood.
            // O prazo é informativo: coluna num formato que não cabe a data fica sem prazo, nunca derruba a importação.
            string? promisedSql = null;
            if (columns.ContainsKey("previsao_entrega"))
                try { promisedSql = TimeSql("previsao_entrega", "@PromisedAtUtc"); } catch (IfoodIntegrationException) { promisedSql = null; }
            var promisedColumn = promisedSql != null ? ",previsao_entrega" : "";
            var promisedValue = promisedSql != null ? "," + promisedSql : "";
            // Canal oficial não passa pelo catálogo, não cria cliente próprio e não preenche id_ifood legado.
            pedido = await connection.ExecuteScalarAsync<int>(new CommandDefinition($"""
INSERT INTO pedido(nome_cliente,endereco_entrega,telefone_cliente,data_pedido,horario_pedido,status_pedido,motoboy_responsavel,
items,value,region,latitude,longitude,entrega_rua,entrega_numero,entrega_bairro,entrega_cidade,entrega_estado,entrega_cep,
tipo_pagamento,observacoes,status_pagamento,codigo_entrega,id_estabelecimento,origem,origem_ref,cliente_id,conversa_id,subtotal,taxa_entrega,desconto{promisedColumn})
VALUES(@CustomerName,@Address,NULL,{TimeSql("data_pedido")},{TimeSql("horario_pedido")},6,NULL,@Items,@OrderAmount,@District,@Latitude,@Longitude,
@Street,@Number,@District,@City,@State,@Zip,@PaymentMethod,@Observations,@PaymentState,NULL,@EstabelecimentoId,'ifood',@Reference,NULL,NULL,@Subtotal,@Fee,@Discount{promisedValue})
RETURNING id
""", new { order.CustomerName, Address = Text("formattedAddress") ?? string.Join(", ", new[] { Text("streetName"), Text("streetNumber"), Text("neighborhood"), Text("city"), Text("state") }.Where(x => x != null)), order.CreatedAtUtc, order.PromisedAtUtc,
                Items = JsonSerializer.Serialize(order.Items.Select(i => $"{i.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture)} {i.Unit} · {i.Name}")),
                order.OrderAmount, order.Latitude, order.Longitude, District = Text("neighborhood") ?? Text("district"), Street = Text("streetName"), Number = Text("streetNumber"),
                City = Text("city"), State = Text("state"), Zip = Text("postalCode") ?? Text("zipCode"),
                PaymentMethod = string.Join(" / ", order.Payments.Select(p => p.Method).Where(m => m != null).Distinct()),
                order.Observations, PaymentState = order.PaymentState, item.EstabelecimentoId,
                Reference = $"{item.MerchantId:D}:{item.ExternalOrderId:D}", Subtotal = LegacyMoney(order.Subtotal), Fee = LegacyMoney(order.DeliveryFee), Discount = LegacyMoney(order.BenefitsAmount)
            }, transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition("""
INSERT INTO ifood_order_links(merchant_id,external_order_id,estabelecimento_id,pedido_id,display_number)
VALUES(@MerchantId,@ExternalOrderId,@EstabelecimentoId,@Pedido,@DisplayNumber)
""", new { item.MerchantId, item.ExternalOrderId, item.EstabelecimentoId, Pedido = pedido, order.DisplayNumber }, transaction, cancellationToken: ct));
            // A loja pode liberar a entrega Zippy pela tela; o pedido novo herda esse valor. Tolera schema sem a coluna.
            // A coluna é verificada antes: o PostgreSQL recusa o comando inteiro se ela não existir, mesmo com EXISTS.
            var hasDeliveryFlag = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=current_schema() AND table_name='ifood_connections' AND column_name='zippy_delivery_enabled')",
                transaction: transaction, cancellationToken: ct));
            if (hasDeliveryFlag)
                await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_order_links l SET zippy_delivery_enabled=c.zippy_delivery_enabled
FROM ifood_connections c
WHERE l.merchant_id=@MerchantId AND l.external_order_id=@ExternalOrderId AND c.merchant_id=l.merchant_id AND c.estabelecimento_id=l.estabelecimento_id AND c.zippy_delivery_enabled
""", new { item.MerchantId, item.ExternalOrderId }, transaction, cancellationToken: ct));
            var validSchedule = order.ScheduledStartUtc != null && order.ScheduledEndUtc != null && order.ScheduledEndUtc >= order.ScheduledStartUtc;
            await connection.ExecuteAsync(new CommandDefinition("""
INSERT INTO ifood_order_snapshots(merchant_id,external_order_id,estabelecimento_id,order_type,delivered_by,remote_status,preparation_state,synchronization_state,
currency,order_amount,prepaid_amount,pending_amount,scheduled_start_utc,scheduled_end_utc,payload)
VALUES(@MerchantId,@ExternalOrderId,@EstabelecimentoId,@OrderType,@DeliveredBy,'PLACED','awaiting_acceptance',@Sync,
@Currency,@OrderAmount,@PrepaidAmount,@PendingAmount,@Start,@End,CAST(@PayloadJson AS jsonb))
""", new { item.MerchantId, item.ExternalOrderId, item.EstabelecimentoId, order.OrderType, order.DeliveredBy,
                Sync = order.RequiresReview ? "review" : recovered ? "pending" : "synced", order.Currency, order.OrderAmount, order.PrepaidAmount, order.PendingAmount,
                Start = validSchedule ? order.ScheduledStartUtc : null, End = validSchedule ? order.ScheduledEndUtc : null,
                PayloadJson = recovered ? RecoveryPayload(order.PayloadJson) : order.PayloadJson }, transaction, cancellationToken: ct));
            var lines = order.Items.Select((line, index) => new { ordinal = index, external_item_id = line.ExternalItemId,
                name = line.Name, quantity = line.Quantity, unit = line.Unit, unit_price = line.UnitPrice, price = line.Price,
                options_price = line.OptionsPrice, total_price = line.TotalPrice, observations = line.Observations, options = line.Options });
            await connection.ExecuteAsync(new CommandDefinition("""
INSERT INTO ifood_order_items(merchant_id,external_order_id,estabelecimento_id,ordinal,external_item_id,name,quantity,unit,unit_price,price,options_price,total_price,observations,options)
SELECT @MerchantId,@ExternalOrderId,@EstabelecimentoId,i.ordinal,i.external_item_id,i.name,i.quantity,i.unit,i.unit_price,i.price,i.options_price,i.total_price,i.observations,i.options
FROM jsonb_to_recordset(CAST(@Lines AS jsonb)) AS i(ordinal int,external_item_id text,name text,quantity numeric,unit text,
unit_price numeric,price numeric,options_price numeric,total_price numeric,observations text,options jsonb)
""", new { item.MerchantId, item.ExternalOrderId, item.EstabelecimentoId, Lines = JsonSerializer.Serialize(lines) }, transaction, cancellationToken: ct));
            var payload = JsonSerializer.Serialize(new { schemaVersion = 2, pedidoId = pedido, estabelecimentoId = item.EstabelecimentoId,
                action = "ifood_imported", occurredAtUtc = DateTimeOffset.UtcNow });
            await connection.ExecuteAsync(new CommandDefinition("""
INSERT INTO delivery_realtime_outbox(event_id,event_name,target_group,estabelecimento_id,motoboy_id,session_id,session_epoch,aggregate_version,payload,occurred_at_utc)
VALUES(@Event,@Name,@Target,@Store,NULL,NULL,NULL,1,CAST(@Payload AS jsonb),NOW())
""", new { Event = Guid.NewGuid(), Name = DeliveryRealtimeEvents.DeliveryOrderUpdated,
                Target = DeliveryRealtimeEvents.EstablishmentGroup(item.EstabelecimentoId), Store = item.EstabelecimentoId, Payload = payload }, transaction, cancellationToken: ct));
        }
        // Fence final usa relógio real mesmo dentro de transação longa; falha desfaz também pedido/outbox.
        var completed = await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_events_inbox SET state=CASE WHEN @Recovered THEN 'queued' ELSE 'processed' END,
processed_at_utc=CASE WHEN @Recovered THEN NULL ELSE NOW() END,lease_id=NULL,lease_until_utc=NULL,
last_error_code=CASE WHEN @Recovered THEN 'IFOOD_RECOVERED_ORDER' ELSE NULL END,
next_attempt_at_utc=NOW(),attempts=CASE WHEN @Recovered THEN 0 ELSE attempts END
WHERE id=@Id AND merchant_id=@MerchantId AND estabelecimento_id=@EstabelecimentoId AND state='processing' AND lease_id=@LeaseId AND lease_until_utc>clock_timestamp()
""", new { item.Id, item.MerchantId, item.EstabelecimentoId, item.LeaseId, Recovered = recovered }, transaction, cancellationToken: ct));
        if (completed != 1) throw new IfoodIntegrationException("IFOOD_EVENT_LEASE_LOST", retryable: true);
        await transaction.CommitAsync(ct);
        await RegisterItemLinksAsync(item, order, created: !existing.HasValue, ct);
        return pedido;
    });

    /// <summary>
    /// Depois do commit, fora da transação crítica: o vínculo só enriquece a exibição, então qualquer falha (inclusive do próprio
    /// serviço) vira log e o pedido continua importado.
    /// </summary>
    private async Task RegisterItemLinksAsync(IfoodInboxReservation item, IfoodImportedOrder order, bool created, CancellationToken ct)
    {
        if (_links == null) return;
        try { await _links.RegisterAsync(item.EstabelecimentoId, item.MerchantId, order.Items, created, ct); }
        catch (Exception ex) { _logger?.LogWarning("Vínculo de itens iFood não registrado: {Error}.", ex.GetType().Name); }
    }

    private static string RecoveryPayload(string json)
    {
        var payload = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        payload["zippyRecovery"] = true;
        return payload.ToJsonString();
    }

    private static async Task EnsureAuthorizedAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid store, Guid merchant, CancellationToken ct)
    {
        var id = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition("""
SELECT merchant_id FROM ifood_connections WHERE merchant_id=@Merchant AND estabelecimento_id=@Store
AND status='authorized' AND authorized_at_utc IS NOT NULL AND revoked_at_utc IS NULL FOR SHARE
""", new { Merchant = merchant, Store = store }, transaction, cancellationToken: ct));
        if (id == null) throw new IfoodIntegrationException("IFOOD_MERCHANT_NOT_AUTHORIZED");
    }
    private sealed class Column { public string Name { get; set; } = ""; public string Type { get; set; } = ""; public int? MaxLength { get; set; } }
    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
        { throw new IfoodIntegrationException("IFOOD_SCHEMA_UNAVAILABLE"); }
        catch (NpgsqlException) { throw new IfoodIntegrationException("IFOOD_IMPORT_DATABASE_ERROR", retryable: true); }
    }
}
