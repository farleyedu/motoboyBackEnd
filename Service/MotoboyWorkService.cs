using System.Text.Json;
using APIBack.DTOs.Delivery;
using Dapper;
using Npgsql;

namespace APIBack.Service;

public sealed class MotoboyWorkService(NpgsqlDataSource source)
{
    public static Task<bool> Available(NpgsqlConnection c, NpgsqlTransaction? tx = null, CancellationToken ct = default) =>
        c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT to_regclass('delivery_rider_work_entries') IS NOT NULL", transaction: tx, cancellationToken: ct));
    public static Task Lock(NpgsqlConnection c, NpgsqlTransaction tx, Guid store, int rider, CancellationToken ct = default) =>
        c.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@Key,0))", new { Key = $"rider-work:{store}:{rider}" }, tx, cancellationToken: ct));
    /// <summary>Esquema do padrão da loja (20261026_01): sem ele só existe a regra individual, como antes.</summary>
    public static Task<bool> DefaultSchema(NpgsqlConnection c, NpgsqlTransaction? tx = null, CancellationToken ct = default) =>
        c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT to_regclass('delivery_rider_plan_resets') IS NOT NULL", transaction: tx, cancellationToken: ct));
    /// <summary>
    /// Exceção individual ATIVA do motoboy: o plano mais recente dele, a não ser que "voltar ao padrão" seja posterior.
    /// Quem nunca teve exceção (ou voltou ao padrão) devolve nulo e segue o padrão da loja.
    /// </summary>
    public static async Task<RiderPayPlan?> Plan(NpgsqlConnection c, NpgsqlTransaction? tx, Guid store, int rider, CancellationToken ct = default) =>
        await DefaultSchema(c, tx, ct)
            ? await c.QuerySingleOrDefaultAsync<RiderPayPlan>(new CommandDefinition("""
SELECT x.id,x.mode,x.rate,x.created_at_utc AS CreatedAtUtc
 FROM (SELECT id,mode,rate,created_at_utc FROM delivery_rider_pay_plans WHERE estabelecimento_id=@Store AND motoboy_id=@Rider ORDER BY created_at_utc DESC,id DESC LIMIT 1) x
 WHERE NOT EXISTS(SELECT 1 FROM delivery_rider_plan_resets r WHERE r.estabelecimento_id=@Store AND r.motoboy_id=@Rider AND r.created_at_utc>x.created_at_utc)
""",new { Store=store,Rider=rider },tx,cancellationToken:ct))
            : await c.QuerySingleOrDefaultAsync<RiderPayPlan>(new CommandDefinition("SELECT id,mode,rate,created_at_utc AS CreatedAtUtc FROM delivery_rider_pay_plans WHERE estabelecimento_id=@Store AND motoboy_id=@Rider ORDER BY created_at_utc DESC,id DESC LIMIT 1",new { Store=store,Rider=rider },tx,cancellationToken:ct));
    /// <summary>Padrão vigente da loja (plano mais recente sem motoboy). Nulo enquanto a loja não definiu o padrão.</summary>
    public static Task<RiderPayPlan?> DefaultPlan(NpgsqlConnection c, NpgsqlTransaction? tx, Guid store, CancellationToken ct = default) =>
        c.QuerySingleOrDefaultAsync<RiderPayPlan>(new CommandDefinition("SELECT id,mode,rate,created_at_utc AS CreatedAtUtc FROM delivery_rider_pay_plans WHERE estabelecimento_id=@Store AND motoboy_id IS NULL ORDER BY created_at_utc DESC,id DESC LIMIT 1",new { Store=store },tx,cancellationToken:ct));
    /// <summary>Regra que vale para o motoboy: a exceção ativa dele; senão o padrão da loja. Source: "rider" | "default" | "none".</summary>
    public static async Task<(RiderPayPlan? Plan,string Source)> EffectivePlan(NpgsqlConnection c, NpgsqlTransaction? tx, Guid store, int rider, CancellationToken ct = default)
    {
        var own=await Plan(c,tx,store,rider,ct);
        if(own!=null) return (own,"rider");
        var standard=await DefaultPlan(c,tx,store,ct);
        return standard!=null?(standard,"default"):(null,"none");
    }
    public static Task LockDefault(NpgsqlConnection c, NpgsqlTransaction tx, Guid store, CancellationToken ct = default) =>
        c.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@Key,0))", new { Key = $"rider-work-default:{store}" }, tx, cancellationToken: ct));
    public static async Task CaptureQuote(NpgsqlConnection c,NpgsqlTransaction tx,Guid store,int rider,int pedido,long stop,CancellationToken ct=default)
    {
        if (!await Available(c,tx,ct)) return;
        await Lock(c,tx,store,rider,ct);
        var (plan,_)=await EffectivePlan(c,tx,store,rider,ct);
        if(plan==null) throw ErrosOperacionais.Excecao(ErrosOperacionais.RiderPayPlanRequired,"Defina a remuneração padrão da loja ou a deste motoboy antes de atribuir entregas.");
        var raw=await c.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT to_jsonb(p)->>'distancia_km' FROM pedido p WHERE id=@Pedido AND id_estabelecimento=@Store",new { Pedido=pedido,Store=store },tx,cancellationToken:ct));
        decimal? distance=decimal.TryParse(raw,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var km)?km:null;
        var quote=RiderPayRules.Quote(plan,distance);
        if (plan.Mode=="distance" && quote.Amount==null)
            throw new DeliveryDomainException(422,"RIDER_DISTANCE_MISSING","A distância deste pedido não foi calculada. Corrija o pedido antes de oferecê-lo com remuneração por distância.");
        await c.ExecuteAsync(new CommandDefinition("INSERT INTO delivery_rider_quotes(stop_id,quote) VALUES(@Stop,CAST(@Quote AS jsonb))",new { Stop=stop,Quote=JsonSerializer.Serialize(quote) },tx,cancellationToken:ct));
    }
    public static async Task RecordDelivery(NpgsqlConnection c,NpgsqlTransaction tx,Guid store,int rider,long stop,int pedido,CancellationToken ct=default)
    {
        if (!await Available(c,tx,ct)) return;
        await Lock(c,tx,store,rider,ct);
        var json=await c.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT quote::text FROM delivery_rider_quotes WHERE stop_id=@Stop",new { Stop=stop },tx,cancellationToken:ct));
        var q=json==null?null:JsonSerializer.Deserialize<RiderPayQuote>(json);
        await c.ExecuteAsync(new CommandDefinition("""
INSERT INTO delivery_rider_work_entries(id,estabelecimento_id,motoboy_id,kind,mode,plan_id,amount,distance_km,pedido_id,stop_id,from_utc,to_utc)
 SELECT @Id,@Store,@Rider,'delivery',@Mode,@Plan,@Amount,@Distance,@Pedido,@Stop,assigned_at_utc,NOW() FROM delivery_route_stops WHERE id=@Stop
 ON CONFLICT(stop_id) DO NOTHING
""",new { Id=Guid.NewGuid(),Store=store,Rider=rider,Stop=stop,Pedido=pedido,Mode=q?.Mode,Plan=q?.PlanId,Amount=q==null?null:q.Mode is "delivery" or "distance"?q.Amount:(decimal?)0,Distance=q?.DistanceKm },tx,cancellationToken:ct));
    }
    private async Task<NpgsqlConnection> Open(CancellationToken ct)
    {
        var c=await source.OpenConnectionAsync(ct);
        if(await Available(c,null,ct)) return c;
        await c.DisposeAsync();
        throw new DeliveryDomainException(503,"WORK_UNAVAILABLE","Histórico financeiro ainda indisponível neste ambiente.");
    }
    public async Task<int> OwnRider(int user,Guid store,CancellationToken ct)
    {
        await using var c=await source.OpenConnectionAsync(ct);
        var id=await c.ExecuteScalarAsync<int?>(new CommandDefinition("""
SELECT m.id FROM motoboy m JOIN usuario u ON u.id=m.id_usuario
 WHERE m.id_usuario=@User AND m.canonical_motoboy_id=m.id AND COALESCE(m.is_simulated,FALSE)=FALSE AND u.deleted_at IS NULL
 AND (EXISTS(SELECT 1 FROM motoboy_estabelecimento me WHERE me.motoboy_id=m.id AND me.estabelecimento_id=@Store)
 OR EXISTS(SELECT 1 FROM delivery_route_stops s WHERE s.motoboy_id=m.id AND s.estabelecimento_id=@Store))
""",new { User=user,Store=store },cancellationToken:ct));
        return id??throw new DeliveryDomainException(404,"RIDER_WORK_NOT_FOUND","Você não possui histórico nesta loja.");
    }
    private static async Task Linked(NpgsqlConnection c,NpgsqlTransaction? tx,Guid store,int rider,CancellationToken ct)
    {
        if(!await c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM motoboy_estabelecimento WHERE estabelecimento_id=@Store AND motoboy_id=@Rider AND ativo=TRUE)",new { Store=store,Rider=rider },tx,cancellationToken:ct)))
            throw new DeliveryDomainException(404,"RIDER_LINK_NOT_FOUND","Motoboy não vinculado a esta loja.");
    }
    public async Task<object> Read(Guid store,int rider,DateTimeOffset from,DateTimeOffset to,CancellationToken ct)
    {
        if(from>=to || (to-from).TotalDays>93) throw new ArgumentException("Escolha um período de até 93 dias.");
        await using var c=await Open(ct);
        var entries=(await c.QueryAsync<RiderWorkEntry>(new CommandDefinition("""
SELECT id,kind,mode,amount,store_cash AS StoreCash,distance_km AS DistanceKm,pedido_id AS PedidoId,stop_id AS StopId,
 settlement_id AS SettlementId,from_utc AS FromUtc,to_utc AS ToUtc,worked_minutes AS WorkedMinutes,
 period_total_seconds AS PeriodTotalSeconds,period_worked_seconds AS PeriodWorkedSeconds,(backfilled_by IS NOT NULL) AS Backfilled
 FROM delivery_rider_work_entries WHERE estabelecimento_id=@Store AND motoboy_id=@Rider AND to_utc>=@From AND to_utc<@To ORDER BY to_utc DESC
""",new { Store=store,Rider=rider,From=from,To=to },cancellationToken:ct))).ToList();
        var settlements=(await c.QueryAsync<RiderSettlement>(new CommandDefinition("""
SELECT id,status,earnings,store_cash AS StoreCash,cash_returned AS CashReturned,reason,method,reference,created_at_utc AS CreatedAtUtc
 FROM delivery_rider_settlements WHERE estabelecimento_id=@Store AND motoboy_id=@Rider
 AND (status NOT IN ('received','cancelled') OR created_at_utc>=@From AND created_at_utc<@To) ORDER BY created_at_utc DESC
""",new { Store=store,Rider=rider,From=from,To=to },cancellationToken:ct))).ToList();
        var balance=await c.QuerySingleAsync(new CommandDefinition("""
SELECT (SELECT COALESCE(SUM(amount),0) FROM delivery_rider_work_entries WHERE estabelecimento_id=@Store AND motoboy_id=@Rider)
 -(SELECT COALESCE(SUM(earnings),0) FROM delivery_rider_settlements WHERE estabelecimento_id=@Store AND motoboy_id=@Rider AND status IN ('paid','received')) AS "outstanding",
 (SELECT COALESCE(SUM(store_cash),0) FROM delivery_rider_work_entries WHERE estabelecimento_id=@Store AND motoboy_id=@Rider)
 -(SELECT COALESCE(SUM(store_cash),0) FROM delivery_rider_settlements WHERE estabelecimento_id=@Store AND motoboy_id=@Rider AND cash_returned=TRUE AND status<>'cancelled') AS "cashToReturn"
""",new { Store=store,Rider=rider },cancellationToken:ct));
        var settlementEntries=await c.QueryAsync<RiderWorkEntry>(new CommandDefinition("""
SELECT id,kind,mode,amount,store_cash AS StoreCash,distance_km AS DistanceKm,pedido_id AS PedidoId,stop_id AS StopId,
 settlement_id AS SettlementId,from_utc AS FromUtc,to_utc AS ToUtc,worked_minutes AS WorkedMinutes,
 period_total_seconds AS PeriodTotalSeconds,period_worked_seconds AS PeriodWorkedSeconds,(backfilled_by IS NOT NULL) AS Backfilled
 FROM delivery_rider_work_entries WHERE estabelecimento_id=@Store AND motoboy_id=@Rider AND settlement_id=ANY(@Ids) ORDER BY to_utc DESC
""",new { Store=store,Rider=rider,Ids=settlements.Select(s=>s.Id).ToArray() },cancellationToken:ct));
        var history=await c.QueryAsync(new CommandDefinition("""
SELECT s.id AS "stopId",s.pedido_id AS "pedidoId",s.stop_status AS "status",s.assigned_at_utc AS "assignedAtUtc",
 s.picked_up_at_utc AS "pickedUpAtUtc",s.arrived_at_utc AS "arrivedAtUtc",s.updated_at_utc AS "updatedAtUtc",
 p.entrega_bairro AS "district",w.amount AS "amount",w.mode AS "mode",w.distance_km AS "distanceKm",
 (w.backfilled_by IS NOT NULL) AS "backfilled",
 d.operation_id AS "operationId", d.receipt-'NomeCliente'-'ProofId' AS "receipt"
 FROM delivery_route_stops s JOIN pedido p ON p.id=s.pedido_id AND p.id_estabelecimento=s.estabelecimento_id
 LEFT JOIN delivery_rider_work_entries w ON w.stop_id=s.id
 LEFT JOIN delivery_completions d ON d.stop_id=s.id AND d.estabelecimento_id=s.estabelecimento_id AND d.motoboy_id=s.motoboy_id
 WHERE s.estabelecimento_id=@Store AND s.motoboy_id=@Rider AND s.updated_at_utc>=@From AND s.updated_at_utc<@To
 AND s.stop_status NOT IN ('assigned','en_route') ORDER BY s.updated_at_utc DESC,s.id DESC
""",new { Store=store,Rider=rider,From=from,To=to },cancellationToken:ct));
        var plans=await c.QueryAsync<RiderPayPlan>(new CommandDefinition("SELECT id,mode,rate,created_at_utc AS CreatedAtUtc FROM delivery_rider_pay_plans WHERE estabelecimento_id=@Store AND motoboy_id=@Rider ORDER BY created_at_utc DESC LIMIT 100",new { Store=store,Rider=rider },cancellationToken:ct));
        var support=await c.QueryAsync(new CommandDefinition("SELECT id,category,message,created_at_utc AS \"createdAtUtc\" FROM delivery_rider_support WHERE estabelecimento_id=@Store AND motoboy_id=@Rider ORDER BY created_at_utc DESC LIMIT 100",new { Store=store,Rider=rider },cancellationToken:ct));
        var unconfirmedReceipts=await c.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*)::int FROM delivery_rider_work_entries WHERE estabelecimento_id=@Store AND motoboy_id=@Rider AND kind='delivery' AND cash_confirmed=FALSE",new { Store=store,Rider=rider },cancellationToken:ct));
        var (effective,source)=await EffectivePlan(c,null,store,rider,ct);
        var standard=await DefaultPlan(c,null,store,ct);
        var resets=await DefaultSchema(c,null,ct)
            ? await c.QueryAsync(new CommandDefinition("SELECT id,created_at_utc AS \"createdAtUtc\" FROM delivery_rider_plan_resets WHERE estabelecimento_id=@Store AND motoboy_id=@Rider ORDER BY created_at_utc DESC LIMIT 100",new { Store=store,Rider=rider },cancellationToken:ct))
            : Array.Empty<object>();
        // Plan = regra que vale (exceção do motoboy ou padrão da loja); PlanSource diz de onde vem; Plans/Resets são o histórico dele.
        return new { Plan=effective,PlanSource=source,DefaultPlan=standard,Plans=plans,Resets=resets,Entries=entries,SettlementEntries=settlementEntries,Settlements=settlements,Balance=balance,History=history,Support=support,Earnings=entries.Sum(e=>e.Amount??0),UnpricedDeliveries=entries.Count(e=>e.Kind=="delivery"&&e.Amount==null),UnconfirmedReceipts=unconfirmedReceipts };
    }
    public const int DefaultSummaryDays = 30;
    /// <summary>Sem from/to: últimos 30 dias até agora (o admin chamava 90 dias por motoboy). Mesmo teto de 93 dias do detalhe.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) SummaryWindow(DateTimeOffset? from,DateTimeOffset? to,DateTimeOffset now)
    {
        var end=to??now;
        var start=from??end.AddDays(-DefaultSummaryDays);
        if(start>=end || (end-start).TotalDays>93) throw new ArgumentException("Escolha um período de até 93 dias.");
        return (start,end);
    }
    private sealed class SummaryRaw
    {
        public int MotoboyId { get; set; } public string? Nome { get; set; }
        public Guid? PlanId { get; set; } public string? PlanMode { get; set; } public decimal? PlanRate { get; set; } public DateTimeOffset? PlanCreatedAtUtc { get; set; }
        public int Entregas { get; set; } public decimal Km { get; set; } public int Minutos { get; set; } public decimal Valor { get; set; }
        public decimal Outstanding { get; set; } public decimal CashToReturn { get; set; } public int Unconfirmed { get; set; }
    }
    /// <summary>
    /// Resumo da loja inteira em uma consulta: uma linha por motoboy vinculado. Totais do período por to_utc (mesma base do detalhe);
    /// saldo e recebimentos não confirmados são do histórico todo, como em <see cref="Read"/>.
    /// </summary>
    public async Task<RiderWorkSummary> Summary(Guid store,DateTimeOffset? from,DateTimeOffset? to,CancellationToken ct)
    {
        var (start,end)=SummaryWindow(from,to,DateTimeOffset.UtcNow);
        await using var c=await Open(ct);
        var lateral=await DefaultSchema(c,null,ct)
            ? "SELECT x.id,x.mode,x.rate,x.created_at_utc FROM (SELECT id,mode,rate,created_at_utc FROM delivery_rider_pay_plans WHERE estabelecimento_id=@Store AND motoboy_id=me.motoboy_id ORDER BY created_at_utc DESC,id DESC LIMIT 1) x WHERE NOT EXISTS(SELECT 1 FROM delivery_rider_plan_resets r WHERE r.estabelecimento_id=@Store AND r.motoboy_id=me.motoboy_id AND r.created_at_utc>x.created_at_utc)"
            : "SELECT id,mode,rate,created_at_utc FROM delivery_rider_pay_plans WHERE estabelecimento_id=@Store AND motoboy_id=me.motoboy_id ORDER BY created_at_utc DESC,id DESC LIMIT 1";
        var rows=await c.QueryAsync<SummaryRaw>(new CommandDefinition(("""
SELECT m.id AS MotoboyId,m.nome::text AS Nome,
 p.id AS PlanId,p.mode AS PlanMode,p.rate AS PlanRate,p.created_at_utc AS PlanCreatedAtUtc,
 COALESCE(pe.entregas,0)::int AS Entregas,COALESCE(pe.km,0) AS Km,COALESCE(pe.minutos,0)::int AS Minutos,COALESCE(pe.valor,0) AS Valor,
 COALESCE(b.earned,0)-COALESCE(s.paid,0) AS Outstanding,
 COALESCE(b.cash,0)-COALESCE(s.cash_returned,0) AS CashToReturn,
 COALESCE(b.unconfirmed,0)::int AS Unconfirmed
 FROM motoboy_estabelecimento me
 JOIN motoboy m ON m.id=me.motoboy_id
 LEFT JOIN LATERAL(/*PLAN*/) p ON TRUE
 LEFT JOIN(SELECT motoboy_id,COUNT(*) FILTER(WHERE kind='delivery') AS entregas,SUM(distance_km) AS km,SUM(worked_minutes) AS minutos,SUM(amount) AS valor
   FROM delivery_rider_work_entries WHERE estabelecimento_id=@Store AND to_utc>=@From AND to_utc<@To GROUP BY motoboy_id) pe ON pe.motoboy_id=me.motoboy_id
 LEFT JOIN(SELECT motoboy_id,SUM(amount) AS earned,SUM(store_cash) AS cash,COUNT(*) FILTER(WHERE kind='delivery' AND cash_confirmed=FALSE) AS unconfirmed
   FROM delivery_rider_work_entries WHERE estabelecimento_id=@Store GROUP BY motoboy_id) b ON b.motoboy_id=me.motoboy_id
 LEFT JOIN(SELECT motoboy_id,SUM(earnings) FILTER(WHERE status IN('paid','received')) AS paid,
   SUM(store_cash) FILTER(WHERE cash_returned=TRUE AND status<>'cancelled') AS cash_returned
   FROM delivery_rider_settlements WHERE estabelecimento_id=@Store GROUP BY motoboy_id) s ON s.motoboy_id=me.motoboy_id
 WHERE me.estabelecimento_id=@Store AND me.ativo=TRUE AND COALESCE(m.is_simulated,FALSE)=FALSE
 ORDER BY COALESCE(pe.valor,0) DESC,m.nome,m.id
""").Replace("/*PLAN*/",lateral),new { Store=store,From=start,To=end },cancellationToken:ct));
        var standard=await DefaultPlan(c,null,store,ct);
        return new RiderWorkSummary(start,end,rows.Select(r=>
        {
            var own=r.PlanId is { } id ? new RiderPayPlan(id,r.PlanMode!,r.PlanRate??0,r.PlanCreatedAtUtc??default) : null;
            var effective=own??standard;
            return new RiderSummaryRow(r.MotoboyId,r.Nome??"",own,
                new RiderSummaryBalance(r.Outstanding,r.CashToReturn),r.Unconfirmed,r.Entregas,r.Km,r.Minutos,r.Valor,
                own!=null?"rider":standard!=null?"default":"none",effective);
        }).ToList(),standard);
    }
    public async Task<Guid> Support(Guid store,int rider,RiderSupportRequest request,CancellationToken ct)
    {
        if(request.OperationId==Guid.Empty||!new[]{"delivery","payment","account","location","security"}.Contains(request.Category)||string.IsNullOrWhiteSpace(request.Message)||request.Message.Length>2000) throw new ArgumentException("Escolha um assunto e descreva o problema em até 2000 caracteres.");
        await using var c=await Open(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await Lock(c,tx,store,rider,ct);
        var previous=await c.QuerySingleOrDefaultAsync<(string Category,string Message)>(new CommandDefinition("SELECT category,message FROM delivery_rider_support WHERE id=@Id AND estabelecimento_id=@Store AND motoboy_id=@Rider",new { Id=request.OperationId,Store=store,Rider=rider },tx,cancellationToken:ct));
        if(previous.Message!=null) { if(previous.Category!=request.Category||previous.Message!=request.Message.Trim()) throw new DeliveryDomainException(409,"IDEMPOTENCY_CONFLICT","Identificação reutilizada com outra solicitação.");await tx.CommitAsync(ct);return request.OperationId; }
        await c.ExecuteAsync(new CommandDefinition("INSERT INTO delivery_rider_support(id,estabelecimento_id,motoboy_id,category,message) VALUES(@Id,@Store,@Rider,@Category,@Message)",new { Id=request.OperationId,Store=store,Rider=rider,request.Category,Message=request.Message.Trim() },tx,cancellationToken:ct));
        await tx.CommitAsync(ct);return request.OperationId;
    }
    public async Task<RiderPayPlan> SavePlan(Guid store,int rider,int user,RiderPayPlanRequest request,CancellationToken ct)
    {
        RiderPayRules.ValidatePlan(request.Mode,request.Rate);
        if(request.OperationId==Guid.Empty) throw new ArgumentException("Identificação ausente.");
        await using var c=await Open(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await Linked(c,tx,store,rider,ct);await Lock(c,tx,store,rider,ct);
        var replay=await c.QuerySingleOrDefaultAsync<RiderPayPlan>(new CommandDefinition("SELECT id,mode,rate,created_at_utc AS CreatedAtUtc FROM delivery_rider_pay_plans WHERE id=@Id AND estabelecimento_id=@Store AND motoboy_id=@Rider",new { Id=request.OperationId,Store=store,Rider=rider },tx,cancellationToken:ct));
        if(replay!=null) { if(replay.Mode!=request.Mode||replay.Rate!=request.Rate) throw new DeliveryDomainException(409,"IDEMPOTENCY_CONFLICT","Identificação reutilizada com outra regra.");await tx.CommitAsync(ct);return replay; }
        var current=await Plan(c,tx,store,rider,ct);
        // Quem herda o padrão confirma o id do padrão que viu (ou nenhum); quem tem exceção confirma o id da exceção.
        var standard=current==null?await DefaultPlan(c,tx,store,ct):null;
        var inUse=current??standard;
        if(current?.Id!=request.ExpectedPlanId && !(current==null && standard!=null && standard.Id==request.ExpectedPlanId)) throw new DeliveryDomainException(409,"PAY_PLAN_CHANGED","A regra mudou. Recarregue antes de salvar.");
        if(inUse!=null && inUse.Mode!=request.Mode && await c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM delivery_route_stops WHERE estabelecimento_id=@Store AND motoboy_id=@Rider AND stop_status IN ('assigned','en_route'))",new { Store=store,Rider=rider },tx,cancellationToken:ct)))
            throw new DeliveryDomainException(409,"PAY_MODE_ACTIVE_ROUTE","Conclua ou retire as entregas e ofertas ativas antes de trocar a modalidade de remuneração. A rota não pode misturar pagamento por entrega e por período.");
        var result=await c.QuerySingleAsync<RiderPayPlan>(new CommandDefinition("INSERT INTO delivery_rider_pay_plans(id,estabelecimento_id,motoboy_id,mode,rate,created_by) VALUES(@Id,@Store,@Rider,@Mode,@Rate,@User) RETURNING id,mode,rate,created_at_utc AS CreatedAtUtc",new { Id=request.OperationId,Store=store,Rider=rider,request.Mode,request.Rate,User=user },tx,cancellationToken:ct));
        await tx.CommitAsync(ct);return result;
    }
    /// <summary>Id do plano de cada motoboy no lote: determinístico por (operação, motoboy), então reenviar o lote não duplica planos.</summary>
    public static Guid BatchPlanId(Guid operationId,int rider) =>
        new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{operationId:N}:{rider}")).AsSpan(0,16));
    /// <summary>
    /// Salva a mesma regra para vários motoboys. Cada motoboy é uma transação própria (como as chamadas individuais de hoje):
    /// quem conflitar (regra mudou, modalidade com rota ativa, sem vínculo) volta com o código, sem desfazer os demais.
    /// </summary>
    public async Task<IReadOnlyList<RiderPayPlanBatchResult>> SavePlans(Guid store,int user,RiderPayPlanBatchRequest request,CancellationToken ct)
    {
        RiderPayRules.ValidatePlan(request.Mode,request.Rate);
        if(request.OperationId==Guid.Empty) throw new ArgumentException("Identificação ausente.");
        if(request.Items.Count is 0 or >200) throw new ArgumentException("Informe de 1 a 200 motoboys.");
        if(request.Items.Select(i=>i.MotoboyId).Distinct().Count()!=request.Items.Count) throw new ArgumentException("Motoboy repetido na lista.");
        var results=new List<RiderPayPlanBatchResult>();
        foreach(var item in request.Items)
        {
            try
            {
                var plan=await SavePlan(store,item.MotoboyId,user,new RiderPayPlanRequest{ OperationId=BatchPlanId(request.OperationId,item.MotoboyId),ExpectedPlanId=item.ExpectedPlanId,Mode=request.Mode,Rate=request.Rate },ct);
                results.Add(new(item.MotoboyId,true,plan,null,null));
            }
            catch(DeliveryDomainException ex){ results.Add(new(item.MotoboyId,false,null,ex.Code,ex.Message)); }
            catch(ArgumentException ex){ results.Add(new(item.MotoboyId,false,null,"VALIDATION_ERROR",ex.Message)); }
        }
        return results;
    }
    private async Task<NpgsqlConnection> OpenDefault(CancellationToken ct)
    {
        var c=await Open(ct);
        if(await DefaultSchema(c,null,ct)) return c;
        await c.DisposeAsync();
        throw new DeliveryDomainException(503,"WORK_UNAVAILABLE","A remuneração padrão ainda não está disponível neste ambiente.");
    }
    private const string RiderHasOverrideSql = """
EXISTS(SELECT 1 FROM (SELECT created_at_utc FROM delivery_rider_pay_plans WHERE estabelecimento_id=@Store AND motoboy_id={0} ORDER BY created_at_utc DESC,id DESC LIMIT 1) x
 WHERE NOT EXISTS(SELECT 1 FROM delivery_rider_plan_resets r WHERE r.estabelecimento_id=@Store AND r.motoboy_id={0} AND r.created_at_utc>x.created_at_utc))
""";
    /// <summary>Alguma entrega ativa de quem hoje segue o padrão? Trocar a modalidade do padrão misturaria pagamento por entrega e por período na mesma rota.</summary>
    private static Task<bool> ActiveRouteOnDefault(NpgsqlConnection c,NpgsqlTransaction tx,Guid store,CancellationToken ct) =>
        c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM delivery_route_stops s WHERE s.estabelecimento_id=@Store AND s.stop_status IN ('assigned','en_route') AND NOT "+string.Format(RiderHasOverrideSql,"s.motoboy_id")+")",new { Store=store },tx,cancellationToken:ct));
    private const string ModeActiveRouteMessage="Conclua ou retire as entregas e ofertas ativas antes de trocar a modalidade de remuneração. A rota não pode misturar pagamento por entrega e por período.";
    /// <summary>Padrão vigente da loja, seu histórico e quantos motoboys o herdam. Funciona sem nenhum motoboy vinculado.</summary>
    public async Task<RiderDefaultPlanView> ReadDefault(Guid store,CancellationToken ct)
    {
        await using var c=await OpenDefault(ct);
        var standard=await DefaultPlan(c,null,store,ct);
        var history=(await c.QueryAsync<RiderPayPlan>(new CommandDefinition("SELECT id,mode,rate,created_at_utc AS CreatedAtUtc FROM delivery_rider_pay_plans WHERE estabelecimento_id=@Store AND motoboy_id IS NULL ORDER BY created_at_utc DESC,id DESC LIMIT 100",new { Store=store },cancellationToken:ct))).ToList();
        var counts=await c.QuerySingleAsync<(int Total,int WithOverride)>(new CommandDefinition("SELECT COUNT(*)::int AS Total,COUNT(*) FILTER(WHERE "+string.Format(RiderHasOverrideSql,"me.motoboy_id")+")::int AS WithOverride FROM motoboy_estabelecimento me JOIN motoboy m ON m.id=me.motoboy_id WHERE me.estabelecimento_id=@Store AND me.ativo=TRUE AND COALESCE(m.is_simulated,FALSE)=FALSE",new { Store=store },cancellationToken:ct));
        return new RiderDefaultPlanView(standard,history,counts.Total-counts.WithOverride,counts.WithOverride);
    }
    /// <summary>
    /// Nova versão do padrão da loja (o histórico nunca é alterado). Afeta só quem não tem exceção, e só as entregas atribuídas daqui em diante:
    /// lançamentos e ofertas já cotadas mantêm o plan_id com que foram calculados. Escolha de projeto: trocar a MODALIDADE com entrega ativa
    /// de alguém que segue o padrão é BLOQUEADO (PAY_MODE_ACTIVE_ROUTE), como na regra individual; mudar só o valor é sempre permitido.
    /// </summary>
    public async Task<RiderPayPlan> SaveDefault(Guid store,int user,RiderPayPlanRequest request,CancellationToken ct)
    {
        RiderPayRules.ValidatePlan(request.Mode,request.Rate);
        if(request.OperationId==Guid.Empty) throw new ArgumentException("Identificação ausente.");
        await using var c=await OpenDefault(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await LockDefault(c,tx,store,ct);
        var replay=await c.QuerySingleOrDefaultAsync<RiderPayPlan>(new CommandDefinition("SELECT id,mode,rate,created_at_utc AS CreatedAtUtc FROM delivery_rider_pay_plans WHERE id=@Id AND estabelecimento_id=@Store AND motoboy_id IS NULL",new { Id=request.OperationId,Store=store },tx,cancellationToken:ct));
        if(replay!=null) { if(replay.Mode!=request.Mode||replay.Rate!=request.Rate) throw new DeliveryDomainException(409,"IDEMPOTENCY_CONFLICT","Identificação reutilizada com outra regra.");await tx.CommitAsync(ct);return replay; }
        var current=await DefaultPlan(c,tx,store,ct);
        if(current?.Id!=request.ExpectedPlanId) throw new DeliveryDomainException(409,"PAY_PLAN_CHANGED","A regra mudou. Recarregue antes de salvar.");
        if(current!=null && current.Mode!=request.Mode && await ActiveRouteOnDefault(c,tx,store,ct))
            throw new DeliveryDomainException(409,"PAY_MODE_ACTIVE_ROUTE",ModeActiveRouteMessage);
        var result=await c.QuerySingleAsync<RiderPayPlan>(new CommandDefinition("INSERT INTO delivery_rider_pay_plans(id,estabelecimento_id,motoboy_id,mode,rate,created_by) VALUES(@Id,@Store,NULL,@Mode,@Rate,@User) RETURNING id,mode,rate,created_at_utc AS CreatedAtUtc",new { Id=request.OperationId,Store=store,request.Mode,request.Rate,User=user },tx,cancellationToken:ct));
        await tx.CommitAsync(ct);return result;
    }
    /// <summary>
    /// "Voltar ao padrão": registra um evento no histórico (nada é apagado) e o motoboy passa a seguir o padrão vigente da loja.
    /// Exige que o padrão exista, senão o motoboy ficaria sem regra. Reenviar a mesma operação devolve o mesmo resultado.
    /// </summary>
    public async Task<RiderOverrideResult> ResetToDefault(Guid store,int rider,int user,Guid operationId,Guid? expectedPlanId,CancellationToken ct)
    {
        if(operationId==Guid.Empty) throw new ArgumentException("Identificação ausente.");
        await using var c=await OpenDefault(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await Linked(c,tx,store,rider,ct);await Lock(c,tx,store,rider,ct);
        var standard=await DefaultPlan(c,tx,store,ct);
        var replay=await c.ExecuteScalarAsync<DateTime?>(new CommandDefinition("SELECT created_at_utc FROM delivery_rider_plan_resets WHERE id=@Id AND estabelecimento_id=@Store AND motoboy_id=@Rider",new { Id=operationId,Store=store,Rider=rider },tx,cancellationToken:ct));
        if(replay.HasValue) { await tx.CommitAsync(ct);return new RiderOverrideResult(rider,(await EffectivePlan(c,null,store,rider,ct)).Source,standard,new DateTimeOffset(DateTime.SpecifyKind(replay.Value,DateTimeKind.Utc))); }
        var own=await Plan(c,tx,store,rider,ct)??throw new DeliveryDomainException(409,"NO_OVERRIDE","Este motoboy já segue a remuneração padrão da loja.");
        if(expectedPlanId.HasValue && own.Id!=expectedPlanId) throw new DeliveryDomainException(409,"PAY_PLAN_CHANGED","A regra mudou. Recarregue antes de salvar.");
        if(standard==null) throw ErrosOperacionais.Excecao(ErrosOperacionais.RiderPayPlanRequired,"Defina a remuneração padrão da loja antes de voltar este motoboy ao padrão.");
        if(own.Mode!=standard.Mode && await c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM delivery_route_stops WHERE estabelecimento_id=@Store AND motoboy_id=@Rider AND stop_status IN ('assigned','en_route'))",new { Store=store,Rider=rider },tx,cancellationToken:ct)))
            throw new DeliveryDomainException(409,"PAY_MODE_ACTIVE_ROUTE",ModeActiveRouteMessage);
        var at=await c.ExecuteScalarAsync<DateTime>(new CommandDefinition("INSERT INTO delivery_rider_plan_resets(id,estabelecimento_id,motoboy_id,created_by) VALUES(@Id,@Store,@Rider,@User) RETURNING created_at_utc",new { Id=operationId,Store=store,Rider=rider,User=user },tx,cancellationToken:ct));
        await tx.CommitAsync(ct);
        return new RiderOverrideResult(rider,"default",standard,new DateTimeOffset(DateTime.SpecifyKind(at,DateTimeKind.Utc)));
    }
    public async Task<Guid> AddPeriod(Guid store,int rider,int user,RiderPeriodRequest request,CancellationToken ct)
    {
        if(request.OperationId==Guid.Empty) throw new ArgumentException("Identificação ausente.");
        await using var c=await Open(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await Linked(c,tx,store,rider,ct);await Lock(c,tx,store,rider,ct);
        var existing=await c.QuerySingleOrDefaultAsync<RiderWorkEntry>(new CommandDefinition("SELECT id,from_utc AS FromUtc,to_utc AS ToUtc,worked_minutes AS WorkedMinutes FROM delivery_rider_work_entries WHERE id=@Id AND estabelecimento_id=@Store AND motoboy_id=@Rider AND plan_id=@Plan",new { Id=request.OperationId,Store=store,Rider=rider,Plan=request.PlanId },tx,cancellationToken:ct));
        if(existing!=null) { if(existing.FromUtc.UtcTicks/10!=request.From.UtcTicks/10||existing.ToUtc.UtcTicks/10!=request.To.UtcTicks/10||existing.WorkedMinutes!=request.WorkedMinutes) throw new DeliveryDomainException(409,"IDEMPOTENCY_CONFLICT","Identificação reutilizada com outro período."); await tx.CommitAsync(ct);return existing.Id; }
        var plan=await c.QuerySingleOrDefaultAsync<RiderPayPlan>(new CommandDefinition("SELECT id,mode,rate,created_at_utc AS CreatedAtUtc FROM delivery_rider_pay_plans WHERE id=@Plan AND estabelecimento_id=@Store AND (motoboy_id=@Rider OR motoboy_id IS NULL)",new { Plan=request.PlanId,Store=store,Rider=rider },tx,cancellationToken:ct))??throw new ArgumentException("Regra não encontrada.");
        var quote=RiderPayRules.PeriodAmount(plan,request);
        var planIsDefault=await c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT motoboy_id IS NULL FROM delivery_rider_pay_plans WHERE id=@Plan",new { Plan=plan.Id },tx,cancellationToken:ct));
        var hasResets=await DefaultSchema(c,tx,ct);
        // O período não pode cruzar uma mudança de regra: nova exceção do motoboy, volta ao padrão e, se o plano usado é o padrão, novo padrão da loja.
        if(await c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM delivery_rider_pay_plans WHERE estabelecimento_id=@Store AND created_at_utc>@Created AND created_at_utc<@To AND (motoboy_id=@Rider OR (@IsDefault AND motoboy_id IS NULL))) OR (@HasResets AND EXISTS(SELECT 1 FROM delivery_rider_plan_resets WHERE estabelecimento_id=@Store AND motoboy_id=@Rider AND created_at_utc>@Created AND created_at_utc<@To)) OR EXISTS(SELECT 1 FROM delivery_rider_work_entries WHERE estabelecimento_id=@Store AND motoboy_id=@Rider AND kind='period' AND from_utc<@To AND to_utc>@From)",new { Store=store,Rider=rider,Created=plan.CreatedAtUtc,request.From,request.To,IsDefault=planIsDefault,HasResets=hasResets },tx,cancellationToken:ct)))
            throw new DeliveryDomainException(409,"PERIOD_OVERLAP","O período cruza outra regra ou já possui remuneração. Não haverá cobrança duplicada.");
        await c.ExecuteAsync(new CommandDefinition("INSERT INTO delivery_rider_work_entries(id,estabelecimento_id,motoboy_id,kind,mode,plan_id,amount,from_utc,to_utc,worked_minutes,period_total_seconds,period_worked_seconds,created_by,cash_confirmed) VALUES(@Id,@Store,@Rider,'period',@Mode,@Plan,@Amount,@From,@To,@Minutes,@TotalSeconds,@WorkedSeconds,@User,TRUE)",new { Id=request.OperationId,Store=store,Rider=rider,Mode=plan.Mode,Plan=plan.Id,Amount=quote.Amount,request.From,request.To,Minutes=request.WorkedMinutes,TotalSeconds=quote.TotalSeconds,WorkedSeconds=quote.WorkedSeconds,User=user },tx,cancellationToken:ct));
        await tx.CommitAsync(ct);return request.OperationId;
    }
    /// <summary>
    /// Aplica a regra atual (só entrega/km) às entregas concluídas antes de existir remuneração registrada
    /// (sem delivery_rider_work_entries). Não inventa dinheiro do cliente: grava só o valor devido ao
    /// motoboy, com store_cash=0 e cash_confirmed=TRUE, e marca backfilled_by para nunca ser confundido
    /// com um lançamento capturado no momento real da atribuição/entrega.
    /// </summary>
    public async Task<object> Backfill(Guid store,int rider,int user,CancellationToken ct)
    {
        await using var c=await Open(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await Linked(c,tx,store,rider,ct);await Lock(c,tx,store,rider,ct);
        var plan=(await EffectivePlan(c,tx,store,rider,ct)).Plan??throw ErrosOperacionais.Excecao(ErrosOperacionais.RiderPayPlanRequired,"Defina a remuneração padrão da loja ou a deste motoboy antes de aplicar aos pedidos antigos.");
        if(plan.Mode is not ("delivery" or "distance")) throw new DeliveryDomainException(422,"RIDER_BACKFILL_MODE_INVALID","A regra atual é por período, não por entrega. Configure uma regra por entrega ou por km antes de aplicar aos pedidos antigos.");
        var candidates=(await c.QueryAsync<(long StopId,int PedidoId,DateTimeOffset FromUtc,DateTimeOffset ToUtc,string? Raw)>(new CommandDefinition("""
SELECT s.id AS StopId,s.pedido_id AS PedidoId,s.assigned_at_utc AS FromUtc,s.completed_at_utc AS ToUtc,to_jsonb(p)->>'distancia_km' AS Raw
 FROM delivery_route_stops s JOIN pedido p ON p.id=s.pedido_id AND p.id_estabelecimento=s.estabelecimento_id
 WHERE s.estabelecimento_id=@Store AND s.motoboy_id=@Rider AND s.stop_status='completed'
 AND NOT EXISTS(SELECT 1 FROM delivery_rider_work_entries w WHERE w.stop_id=s.id)
""",new { Store=store,Rider=rider },tx,cancellationToken:ct))).ToList();
        int inserted=0,skipped=0;decimal total=0;
        foreach(var row in candidates)
        {
            var distance=decimal.TryParse(row.Raw,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var km)?km:(decimal?)null;
            var quote=RiderPayRules.Quote(plan,distance);
            if(quote.Amount==null){skipped++;continue;}
            await c.ExecuteAsync(new CommandDefinition("""
INSERT INTO delivery_rider_work_entries(id,estabelecimento_id,motoboy_id,kind,mode,plan_id,amount,distance_km,pedido_id,stop_id,from_utc,to_utc,cash_confirmed,backfilled_by)
 VALUES(@Id,@Store,@Rider,'delivery',@Mode,@Plan,@Amount,@Distance,@Pedido,@Stop,@From,@To,TRUE,@User)
 ON CONFLICT(stop_id) DO NOTHING
""",new { Id=Guid.NewGuid(),Store=store,Rider=rider,Stop=row.StopId,Pedido=row.PedidoId,Mode=plan.Mode,Plan=plan.Id,Amount=quote.Amount,Distance=quote.DistanceKm,From=row.FromUtc,To=row.ToUtc,User=user },tx,cancellationToken:ct));
            inserted++;total+=quote.Amount.Value;
        }
        await tx.CommitAsync(ct);
        return new { Inserted=inserted,Skipped=skipped,TotalAmount=RiderPayRules.Money(total) };
    }
    public async Task<Guid> CreateSettlement(Guid store,int rider,int user,Guid id,CancellationToken ct)
    {
        if(id==Guid.Empty) throw new ArgumentException("Identificação ausente.");
        await using var c=await Open(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await Linked(c,tx,store,rider,ct);await Lock(c,tx,store,rider,ct);
        if(await c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM delivery_rider_settlements WHERE id=@Id AND estabelecimento_id=@Store AND motoboy_id=@Rider)",new { Id=id,Store=store,Rider=rider },tx,cancellationToken:ct))) { await tx.CommitAsync(ct);return id; }
        var totals=await c.QuerySingleAsync<(int Count,int Unknown,decimal Amount,decimal Cash)>(new CommandDefinition("SELECT COUNT(*)::int AS Count,COUNT(*) FILTER(WHERE amount IS NULL OR cash_confirmed=FALSE)::int AS Unknown,COALESCE(SUM(amount),0) AS Amount,COALESCE(SUM(store_cash),0) AS Cash FROM delivery_rider_work_entries WHERE estabelecimento_id=@Store AND motoboy_id=@Rider AND settlement_id IS NULL",new { Store=store,Rider=rider },tx,cancellationToken:ct));
        if(totals.Count==0||totals.Unknown>0) throw new DeliveryDomainException(422,"SETTLEMENT_INCOMPLETE",totals.Unknown>0?"Existem entregas sem remuneração ou recebimento conferido. Não é possível tratá-las como valor zero.":"Não há lançamentos novos para acertar.");
        await c.ExecuteAsync(new CommandDefinition("INSERT INTO delivery_rider_settlements(id,estabelecimento_id,motoboy_id,earnings,store_cash,created_by) VALUES(@Id,@Store,@Rider,@Amount,@Cash,@User); UPDATE delivery_rider_work_entries SET settlement_id=@Id WHERE estabelecimento_id=@Store AND motoboy_id=@Rider AND settlement_id IS NULL",new { Id=id,Store=store,Rider=rider,totals.Amount,totals.Cash,User=user },tx,cancellationToken:ct));
        await tx.CommitAsync(ct);return id;
    }
    public async Task<bool> Action(Guid store,int rider,int user,Guid id,RiderSettlementAction request,bool owner,CancellationToken ct)
    {
        await using var c=await Open(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await Lock(c,tx,store,rider,ct);
        var row=await c.QuerySingleOrDefaultAsync<RiderSettlement>(new CommandDefinition("SELECT id,status,earnings,store_cash AS StoreCash,cash_returned AS CashReturned,reason,method,reference FROM delivery_rider_settlements WHERE id=@Id AND estabelecimento_id=@Store AND motoboy_id=@Rider FOR UPDATE",new { Id=id,Store=store,Rider=rider },tx,cancellationToken:ct))??throw new DeliveryDomainException(404,"SETTLEMENT_NOT_FOUND","Acerto não encontrado.");
        if(request.Reason?.Length>1000||request.Reference?.Length>200) throw new ArgumentException("Texto excede o limite permitido.");
        var cash=request.Action=="cash-return";
        var replay=(request.Action=="receive"&&row.Status=="received"&&owner)||(request.Action=="review"&&row.Status=="reviewed"&&owner)||(request.Action=="dispute"&&row.Status=="disputed"&&owner&&row.Reason==request.Reason)||(request.Action=="pay"&&row.Status=="paid"&&!owner&&row.Method==request.Method&&row.Reference==request.Reference)||(request.Action=="cancel"&&row.Status=="cancelled"&&!owner)||(cash&&!owner&&row.CashReturned);
        if(replay){await tx.CommitAsync(ct);return true;}
        if(cash && (owner||row.Status=="cancelled"||row.StoreCash<=0)) throw new ArgumentException("Devolução de dinheiro deve ser confirmada pela loja em um acerto válido.");
        if(request.Action=="cancel"&&row.CashReturned) throw new ArgumentException("Um acerto com devolução registrada não pode ser cancelado.");
        var status=cash?row.Status:RiderPayRules.NextStatus(row.Status,request.Action,owner);
        if(request.Action=="dispute"&&string.IsNullOrWhiteSpace(request.Reason)) throw new ArgumentException("Descreva a divergência.");
        if(request.Action=="pay"&&(!new[]{"pix","cash","transfer","other"}.Contains(request.Method)||string.IsNullOrWhiteSpace(request.Reference))) throw new ArgumentException("Informe o meio e a referência do pagamento efetivamente realizado. Este registro não transfere dinheiro.");
        await c.ExecuteAsync(new CommandDefinition("""
UPDATE delivery_rider_settlements SET status=@Status,cash_returned=cash_returned OR @Cash,
 reason=CASE WHEN @Action='dispute' THEN @Reason ELSE reason END,
 method=CASE WHEN @Action='pay' THEN @Method ELSE method END,reference=CASE WHEN @Action='pay' THEN @Reference ELSE reference END WHERE id=@Id;
INSERT INTO delivery_rider_settlement_events(settlement_id,actor_user_id,action,details) VALUES(@Id,@User,@Action,CAST(@Details AS jsonb));
""",new { Id=id,User=user,Status=status,Cash=cash,request.Action,request.Reason,request.Method,request.Reference,Details=JsonSerializer.Serialize(request) },tx,cancellationToken:ct));
        if(status=="cancelled") await c.ExecuteAsync(new CommandDefinition("UPDATE delivery_rider_work_entries SET settlement_id=NULL WHERE settlement_id=@Id",new { Id=id },tx,cancellationToken:ct));
        await tx.CommitAsync(ct);return true;
    }
}
