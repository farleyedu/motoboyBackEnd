using System.Text.Json;
using APIBack.DTOs.Delivery;
using Dapper;
using Npgsql;

namespace APIBack.Service;

public sealed class MotoboyRouteHistoryService(NpgsqlDataSource source)
{
    public sealed record RouteRow(Guid Id, string Status, DateTimeOffset StartedAtUtc, DateTimeOffset? EndedAtUtc, int OrderCount, int CompletedCount, decimal Earnings, decimal StoreCash, int UnpricedCount);
    public sealed class StopRow
    {
        public long StopId { get; set; } public int PedidoId { get; set; } public int Position { get; set; }
        public string Status { get; set; } = ""; public string? District { get; set; }
        public double? Latitude { get; set; } public double? Longitude { get; set; }
        public DateTimeOffset? PickedUpAtUtc { get; set; } public DateTimeOffset? ArrivedAtUtc { get; set; } public DateTimeOffset UpdatedAtUtc { get; set; }
        public decimal? Amount { get; set; } public decimal StoreCash { get; set; } public string? Mode { get; set; }
        public Guid? OperationId { get; set; } public string? Manifest { get; set; } public string? Pickup { get; set; } public string? Delivery { get; set; } public string? Receipt { get; set; }
    }
    public sealed record Point(double Lat, double Lng, DateTimeOffset CapturedAtUtc);
    private async Task<NpgsqlConnection> Open(CancellationToken ct)
    {
        var db = await source.OpenConnectionAsync(ct);
        if (await db.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT to_regclass('delivery_route_runs') IS NOT NULL", cancellationToken:ct))) return db;
        await db.DisposeAsync(); throw new DeliveryDomainException(503,"ROUTE_HISTORY_UNAVAILABLE","O histórico de rotas ainda não foi habilitado neste ambiente.");
    }
    private const string Summary = """
SELECT r.id,r.status,r.started_at_utc AS StartedAtUtc,r.ended_at_utc AS EndedAtUtc,
 COUNT(h.stop_id)::int AS OrderCount,COUNT(h.stop_id) FILTER(WHERE h.status='completed')::int AS CompletedCount,
 COALESCE(SUM(w.amount),0) AS Earnings,COALESCE(SUM(w.store_cash),0) AS StoreCash,
 COUNT(h.stop_id) FILTER(WHERE h.status='completed' AND w.amount IS NULL)::int AS UnpricedCount
 FROM delivery_route_runs r LEFT JOIN delivery_route_run_stops h ON h.run_id=r.id
 LEFT JOIN delivery_rider_work_entries w ON w.stop_id=h.stop_id AND w.estabelecimento_id=r.estabelecimento_id AND w.motoboy_id=r.motoboy_id
""";
    public async Task<object> List(Guid store,int rider,DateTimeOffset from,DateTimeOffset to,int offset,CancellationToken ct)
    {
        if(from>=to || (to-from).TotalDays>93 || offset<0 || offset>10000) throw new ArgumentException("Escolha um período de até 93 dias.");
        await using var db=await Open(ct);
        var rows=(await db.QueryAsync<RouteRow>(new CommandDefinition(Summary+" WHERE r.estabelecimento_id=@Store AND r.motoboy_id=@Rider AND r.started_at_utc>=@From AND r.started_at_utc<@To GROUP BY r.id ORDER BY r.started_at_utc DESC,r.id DESC LIMIT 31 OFFSET @Offset",new {Store=store,Rider=rider,From=from.ToUniversalTime(),To=to.ToUniversalTime(),Offset=offset},cancellationToken:ct))).ToList();
        return new { Routes=rows.Take(30), HasMore=rows.Count>30, NextOffset=offset+30 };
    }
    public async Task<object> Detail(Guid store,int rider,Guid id,CancellationToken ct)
    {
        await using var db=await Open(ct);
        var route=await db.QuerySingleOrDefaultAsync<RouteRow>(new CommandDefinition(Summary+" WHERE r.id=@Id AND r.estabelecimento_id=@Store AND r.motoboy_id=@Rider GROUP BY r.id",new{Id=id,Store=store,Rider=rider},cancellationToken:ct))??throw new DeliveryDomainException(404,"ROUTE_NOT_FOUND","Esta rota não pertence ao seu histórico.");
        var stops=(await db.QueryAsync<StopRow>(new CommandDefinition("""
SELECT h.stop_id AS StopId,h.pedido_id AS PedidoId,h.position,h.status,h.district,h.latitude,h.longitude,
 h.picked_up_at_utc AS PickedUpAtUtc,h.arrived_at_utc AS ArrivedAtUtc,h.updated_at_utc AS UpdatedAtUtc,
 w.amount,w.mode,w.store_cash AS StoreCash,d.operation_id AS OperationId,h.manifest::text AS Manifest,
 (SELECT confirmation::text FROM delivery_order_item_checks WHERE stop_id=h.stop_id AND stage='pickup' AND estabelecimento_id=@Store AND motoboy_id=@Rider) AS Pickup,
 (SELECT confirmation::text FROM delivery_order_item_checks WHERE stop_id=h.stop_id AND stage='delivery' AND estabelecimento_id=@Store AND motoboy_id=@Rider) AS Delivery,
 (d.receipt-'NomeCliente'-'ProofId')::text AS Receipt
 FROM delivery_route_run_stops h LEFT JOIN delivery_rider_work_entries w ON w.stop_id=h.stop_id AND w.estabelecimento_id=@Store AND w.motoboy_id=@Rider
 LEFT JOIN delivery_completions d ON d.stop_id=h.stop_id AND d.estabelecimento_id=@Store AND d.motoboy_id=@Rider
 WHERE h.run_id=@Id ORDER BY h.position,h.stop_id
""",new{Id=id,Store=store,Rider=rider},cancellationToken:ct))).ToList();
        // Redução determinística de amostras reais, sem rotear novamente um percurso histórico.
        var points=(await db.QueryAsync<Point>(new CommandDefinition("""
WITH numbered AS (SELECT latitude AS Lat,longitude AS Lng,captured_at_utc AS CapturedAtUtc,
 ROW_NUMBER() OVER(ORDER BY captured_at_utc,id) AS n,COUNT(*) OVER() AS total FROM delivery_route_run_points WHERE run_id=@Id)
 SELECT Lat,Lng,CapturedAtUtc FROM numbered WHERE n=1 OR n=total OR MOD(n,GREATEST(1,CEIL(total/2000.0)::bigint))=0 ORDER BY CapturedAtUtc
""",new{Id=id},cancellationToken:ct))).ToList();
        var segments=new List<List<Point>>();
        foreach(var point in points)
        {
            if(segments.Count==0 || point.CapturedAtUtc-segments[^1][^1].CapturedAtUtc>TimeSpan.FromMinutes(2)) segments.Add(new());
            segments[^1].Add(point);
        }
        return new { Route=route,Stops=stops.Select(s=>new {s.StopId,s.PedidoId,s.Position,s.Status,s.District,s.Latitude,s.Longitude,s.PickedUpAtUtc,s.ArrivedAtUtc,s.UpdatedAtUtc,s.Amount,s.Mode,s.StoreCash,s.OperationId,Manifest=Json<DeliveryChecklist>(s.Manifest),Pickup=Json<DeliveryChecklistConfirmation>(s.Pickup),Delivery=Json<DeliveryChecklistConfirmation>(s.Delivery),Receipt=Json<HistoryReceipt>(s.Receipt)}),Segments=segments,PathAvailable=segments.Any(s=>s.Count>1) };
    }
    public sealed record HistoryReceipt(bool CodeChecked,bool PaidBeforeDelivery,DateTimeOffset CompletedAtUtc,List<DeliveryPaymentPart> Payments);
    private static T? Json<T>(string? value) where T:class=>value==null?null:JsonSerializer.Deserialize<T>(value);
}
