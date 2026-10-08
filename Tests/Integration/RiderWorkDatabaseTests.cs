using System.Text.Json;
using APIBack.DTOs.Delivery;
using APIBack.Repository;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Integration;
public partial class DeliverySyncDatabaseTests
{
    private static async Task<Database> WorkDatabase()
    {
        var db=await CompletionDatabase();
        await db.Execute("""
ALTER TABLE pedido ADD COLUMN distancia_km numeric;
UPDATE pedido SET distancia_km=3.334;
ALTER TABLE motoboy ADD COLUMN id_usuario int;
UPDATE motoboy SET id_usuario=7;
CREATE TABLE usuario(id integer PRIMARY KEY,deleted_at timestamptz);
INSERT INTO usuario VALUES(7,NULL),(8,NULL);
UPDATE motoboy_active_sessions SET expires_at_utc=NOW()+interval '1 hour';
""");
        var migration=await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Migrations","Delivery","20261008_08_motoboy_work.sql"));
        await db.Execute(migration);await db.Execute(migration);
        var periodo=await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Migrations","Delivery","20261008_10_motoboy_work_periodo_parcial.sql"));
        await db.Execute(periodo);await db.Execute(periodo);
        return db;
    }
    private static async Task<RiderPayPlan> WorkPlan(Database db,string mode="distance",decimal rate=2.50m)
    {
        var service=new MotoboyWorkService(db.Source);
        return await service.SavePlan(db.StoreId,1,9,new(){OperationId=Guid.NewGuid(),Mode=mode,Rate=rate},default);
    }
    private static async Task CaptureWork(Database db,long stop=1,int pedido=23)
    {
        await using var c=await db.Source.OpenConnectionAsync();await using var tx=await c.BeginTransactionAsync();
        await MotoboyWorkService.CaptureQuote(c,tx,db.StoreId,1,pedido,stop);await tx.CommitAsync();
    }
    [DeliveryDatabaseFact] public async Task WorkQuotesFreezeAndCompletionCashDoesNotIncludeCustomerChange()
    {
        await using var db=await WorkDatabase();var service=new MotoboyWorkService(db.Source);var original=await WorkPlan(db);await CaptureWork(db);
        await service.SavePlan(db.StoreId,1,9,new(){OperationId=Guid.NewGuid(),ExpectedPlanId=original.Id,Mode="distance",Rate=8m},default);
        var repo=new PedidoQueueRepository(db.Source);
        var queue=await repo.GetQueueAsync(db.StoreId,1);Assert.Equal(8.34m,queue.Current!.Earnings!.Amount);
        var request=CompletionRequest();await repo.CompleteDeliveryAsync(db.StoreId,1,7,db.SessionId,4,request,default);
        await repo.CompleteDeliveryAsync(db.StoreId,1,7,db.SessionId,4,request,default);
        Assert.Equal(8.34m,await db.Scalar<decimal>("SELECT amount FROM delivery_rider_work_entries"));
        Assert.Equal(66.90m,await db.Scalar<decimal>("SELECT store_cash FROM delivery_rider_work_entries"));
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_rider_work_entries"));
        var work=JsonSerializer.Serialize(await service.Read(db.StoreId,1,DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddMinutes(1),default));
        Assert.DoesNotContain("NomeCliente",work);Assert.Contains("8.34",work);
    }
    [DeliveryDatabaseFact] public async Task WorkSettlementRequiresReviewAndCashReturnIsIndependentOfPay()
    {
        await using var db=await WorkDatabase();await WorkPlan(db,"delivery",9.50m);await CaptureWork(db);
        await new PedidoQueueRepository(db.Source).CompleteDeliveryAsync(db.StoreId,1,7,db.SessionId,4,CompletionRequest(),default);
        var service=new MotoboyWorkService(db.Source);var id=Guid.NewGuid();
        var ids=await Task.WhenAll(Enumerable.Range(0,3).Select(_=>service.CreateSettlement(db.StoreId,1,9,id,default)));Assert.All(ids,x=>Assert.Equal(id,x));
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>service.Action(db.StoreId,1,9,id,new(){Action="pay",Method="pix",Reference="receipt"},false,default));
        await service.Action(db.StoreId,1,7,id,new(){Action="dispute",Reason="Quero conferir"},true,default);
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>service.Action(db.StoreId,1,9,id,new(){Action="pay",Method="pix",Reference="receipt"},false,default));
        await service.Action(db.StoreId,1,7,id,new(){Action="review"},true,default);
        await service.Action(db.StoreId,1,9,id,new(){Action="pay",Method="pix",Reference="receipt"},false,default);
        await service.Action(db.StoreId,1,9,id,new(){Action="pay",Method="pix",Reference="receipt"},false,default);
        Assert.False(await db.Scalar<bool>("SELECT cash_returned FROM delivery_rider_settlements"));
        await service.Action(db.StoreId,1,7,id,new(){Action="receive"},true,default);
        await service.Action(db.StoreId,1,9,id,new(){Action="cash-return"},false,default);
        Assert.Equal("received",await db.Scalar<string>("SELECT status FROM delivery_rider_settlements"));
        Assert.Equal(5,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_rider_settlement_events"));
    }
    [DeliveryDatabaseFact] public async Task WorkIdentityAndMutationsAreRestrictedToTenantAndRider()
    {
        await using var db=await WorkDatabase();var service=new MotoboyWorkService(db.Source);var p=await WorkPlan(db);
        Assert.Equal(1,await service.OwnRider(7,db.StoreId,default));
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>service.OwnRider(8,db.StoreId,default));
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>service.OwnRider(7,Guid.NewGuid(),default));
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>service.SavePlan(Guid.NewGuid(),1,9,new(){OperationId=Guid.NewGuid(),Mode="delivery",Rate=10},default));
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>service.SavePlan(db.StoreId,1,9,new(){OperationId=Guid.NewGuid(),Mode="delivery",Rate=10},default));
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>service.Action(Guid.NewGuid(),1,9,Guid.NewGuid(),new(){Action="receive"},true,default));
        var support=new RiderSupportRequest{OperationId=Guid.NewGuid(),Category="security",Message="Local inseguro"};
        await service.Support(db.StoreId,1,support,default);await service.Support(db.StoreId,1,support,default);
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_rider_support"));
        support.Message="Outra solicitação";await Assert.ThrowsAsync<DeliveryDomainException>(()=>service.Support(db.StoreId,1,support,default));
    }
    [DeliveryDatabaseFact] public async Task WorkOfferRequiresSameFinancialVersionAndRefusalCreatesNoEarning()
    {
        await using var db=await WorkDatabase();await WorkPlan(db);await CaptureWork(db);await CaptureWork(db,2,24);
        var offer=Guid.NewGuid();await db.Execute("UPDATE delivery_route_stops SET stop_status='assigned',offer_id=@Offer,offered_at_utc=NOW(); UPDATE delivery_settings SET require_motoboy_acceptance=TRUE;",new { Offer=offer });
        var repo=new PedidoQueueRepository(db.Source);
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.AcceptOfferForAsync(db.StoreId,1,offer));
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.AcceptPricedOfferAsync(db.StoreId,1,offer,2));
        Assert.Equal(2,(await repo.AcceptPricedOfferAsync(db.StoreId,1,offer,3)).Next.Count+1);
        Assert.Equal(0,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_rider_work_entries"));
    }
    [DeliveryDatabaseFact] public async Task WorkHourlyPeriodsAreIdempotentAndRejectOverlaps()
    {
        await using var db=await WorkDatabase();var plan=await WorkPlan(db,"hour",20);await db.Execute("UPDATE delivery_rider_pay_plans SET created_at_utc=NOW()-interval '2 days'");
        var service=new MotoboyWorkService(db.Source);var r=new RiderPeriodRequest{OperationId=Guid.NewGuid(),PlanId=plan.Id,From=DateTimeOffset.UtcNow.AddHours(-4),To=DateTimeOffset.UtcNow.AddHours(-1),WorkedMinutes=120};
        await service.AddPeriod(db.StoreId,1,9,r,default);await service.AddPeriod(db.StoreId,1,9,r,default);
        Assert.Equal(40m,await db.Scalar<decimal>("SELECT amount FROM delivery_rider_work_entries"));
        r.OperationId=Guid.NewGuid();await Assert.ThrowsAsync<DeliveryDomainException>(()=>service.AddPeriod(db.StoreId,1,9,r,default));
    }
    [DeliveryDatabaseFact] public async Task WorkPartialWeekIsProratedPersistedAndVisibleToTheRider()
    {
        await using var db=await WorkDatabase();var plan=await WorkPlan(db,"week",350m);
        await db.Execute("UPDATE delivery_rider_pay_plans SET created_at_utc='2026-01-05T00:00:00-03:00'::timestamptz");
        var service=new MotoboyWorkService(db.Source);
        // Segunda (05/01) a quinta (08/01): 3 dos 7 dias da semana - motoboy começou no meio da semana.
        var r=new RiderPeriodRequest{OperationId=Guid.NewGuid(),PlanId=plan.Id,From=DateTimeOffset.Parse("2026-01-05T00:00:00-03:00"),To=DateTimeOffset.Parse("2026-01-08T00:00:00-03:00")};
        await service.AddPeriod(db.StoreId,1,9,r,default);
        Assert.Equal(150m,await db.Scalar<decimal>("SELECT amount FROM delivery_rider_work_entries"));
        Assert.Equal(7*86400L,await db.Scalar<long>("SELECT period_total_seconds FROM delivery_rider_work_entries"));
        Assert.Equal(3*86400L,await db.Scalar<long>("SELECT period_worked_seconds FROM delivery_rider_work_entries"));
        var json=JsonSerializer.Serialize(await service.Read(db.StoreId,1,DateTimeOffset.UtcNow.AddDays(-400),DateTimeOffset.UtcNow.AddMinutes(1),default));
        Assert.Contains("\"PeriodTotalSeconds\":604800",json);Assert.Contains("\"PeriodWorkedSeconds\":259200",json);Assert.Contains("\"Backfilled\":false",json);
        // Registrar o resto da mesma semana (quinta a segunda seguinte) não pode se sobrepor ao pedaço já pago.
        var rest=new RiderPeriodRequest{OperationId=Guid.NewGuid(),PlanId=plan.Id,From=DateTimeOffset.Parse("2026-01-08T00:00:00-03:00"),To=DateTimeOffset.Parse("2026-01-12T00:00:00-03:00")};
        await service.AddPeriod(db.StoreId,1,9,rest,default);
        Assert.Equal(200m,await db.Scalar<decimal>($"SELECT amount FROM delivery_rider_work_entries WHERE id='{rest.OperationId}'"));
        // Cruzar pra semana seguinte (segunda 12/01 é o fim; estender até 13/01 invade a próxima ocorrência).
        var invalid=new RiderPeriodRequest{OperationId=Guid.NewGuid(),PlanId=plan.Id,From=DateTimeOffset.Parse("2026-01-05T00:00:00-03:00"),To=DateTimeOffset.Parse("2026-01-13T00:00:00-03:00")};
        await Assert.ThrowsAsync<ArgumentException>(()=>service.AddPeriod(db.StoreId,1,9,invalid,default));
    }
    [DeliveryDatabaseFact] public async Task BackfillPricesOldCompletedStopsWithoutInventingCashOrCrossingPeriodModes()
    {
        await using var db=await WorkDatabase();var service=new MotoboyWorkService(db.Source);
        // Pedido concluído antes de existir remuneração registrada: sem quote, sem delivery_rider_work_entries.
        await db.Execute("UPDATE delivery_route_stops SET stop_status='completed',completed_at_utc=NOW() WHERE id=1");
        var missing=await Assert.ThrowsAsync<DeliveryDomainException>(()=>service.Backfill(db.StoreId,1,9,default));
        Assert.Equal("RIDER_PAY_PLAN_REQUIRED",missing.Code);
        await WorkPlan(db,"week",350m);
        var invalidMode=await Assert.ThrowsAsync<DeliveryDomainException>(()=>service.Backfill(db.StoreId,1,9,default));
        Assert.Equal("RIDER_BACKFILL_MODE_INVALID",invalidMode.Code);
        await db.Execute("DELETE FROM delivery_rider_pay_plans");await WorkPlan(db,"distance",2.50m);
        var first=JsonSerializer.Serialize(await service.Backfill(db.StoreId,1,9,default));
        Assert.Contains("\"Inserted\":1",first);Assert.Contains("\"Skipped\":0",first);Assert.Contains("\"TotalAmount\":8.34",first);
        Assert.Equal(8.34m,await db.Scalar<decimal>("SELECT amount FROM delivery_rider_work_entries WHERE stop_id=1"));
        Assert.Equal(0m,await db.Scalar<decimal>("SELECT store_cash FROM delivery_rider_work_entries WHERE stop_id=1"));
        Assert.True(await db.Scalar<bool>("SELECT cash_confirmed FROM delivery_rider_work_entries WHERE stop_id=1"));
        Assert.True(await db.Scalar<bool>("SELECT backfilled_by IS NOT NULL FROM delivery_rider_work_entries WHERE stop_id=1"));
        // Idempotente: rodar de novo não duplica nem cobra duas vezes o mesmo pedido antigo.
        var second=JsonSerializer.Serialize(await service.Backfill(db.StoreId,1,9,default));
        Assert.Contains("\"Inserted\":0",second);
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_rider_work_entries"));
    }
    [DeliveryDatabaseFact] public async Task WorkRejectsMissingPlanAndDistanceInsteadOfInventingZero()
    {
        await using var db=await WorkDatabase();
        var missing=await Assert.ThrowsAsync<DeliveryDomainException>(()=>CaptureWork(db));
        Assert.Equal("RIDER_PAY_PLAN_REQUIRED",missing.Code);
        await WorkPlan(db);await db.Execute("UPDATE pedido SET distancia_km=NULL WHERE id=23");
        var distance=await Assert.ThrowsAsync<DeliveryDomainException>(()=>CaptureWork(db));
        Assert.Equal("RIDER_DISTANCE_MISSING",distance.Code);
        Assert.Equal(0,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_rider_quotes"));
    }
    [DeliveryDatabaseFact] public async Task WorkModeCannotChangeDuringActiveRouteButRateCan()
    {
        await using var db=await WorkDatabase();var original=await WorkPlan(db);var service=new MotoboyWorkService(db.Source);
        var error=await Assert.ThrowsAsync<DeliveryDomainException>(()=>service.SavePlan(db.StoreId,1,9,new(){OperationId=Guid.NewGuid(),ExpectedPlanId=original.Id,Mode="hour",Rate=20},default));
        Assert.Equal("PAY_MODE_ACTIVE_ROUTE",error.Code);
        var updated=await service.SavePlan(db.StoreId,1,9,new(){OperationId=Guid.NewGuid(),ExpectedPlanId=original.Id,Mode="distance",Rate=3},default);
        await db.Execute("UPDATE delivery_route_stops SET stop_status='removed'");
        Assert.Equal("hour",(await service.SavePlan(db.StoreId,1,9,new(){OperationId=Guid.NewGuid(),ExpectedPlanId=updated.Id,Mode="hour",Rate=20},default)).Mode);
    }
    [DeliveryDatabaseFact] public async Task WorkBlocksUnconfirmedCashAndCancelledSettlementReleasesEntries()
    {
        await using var db=await WorkDatabase();await WorkPlan(db,"delivery",10);await CaptureWork(db);
        await using(var c=await db.Source.OpenConnectionAsync())
        await using(var tx=await c.BeginTransactionAsync())
        { await MotoboyWorkService.RecordDelivery(c,tx,db.StoreId,1,1,23);await tx.CommitAsync(); }
        var service=new MotoboyWorkService(db.Source);
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>service.CreateSettlement(db.StoreId,1,9,Guid.NewGuid(),default));
        await db.Execute("UPDATE delivery_rider_work_entries SET cash_confirmed=TRUE");
        var original=await service.CreateSettlement(db.StoreId,1,9,Guid.NewGuid(),default);
        await service.Action(db.StoreId,1,9,original,new(){Action="cancel"},false,default);
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_rider_work_entries WHERE settlement_id IS NULL"));
        var next=await service.CreateSettlement(db.StoreId,1,9,Guid.NewGuid(),default);
        Assert.NotEqual(original,next);
        Assert.Equal(10m,await db.Scalar<decimal>("SELECT SUM(earnings) FROM delivery_rider_settlements WHERE status<>'cancelled'"));
    }
    [DeliveryDatabaseFact] public async Task WorkMigrationVerifiesAndRollbackOnlyAllowsEmptyTables()
    {
        await using var db=await WorkDatabase();
        var root=Path.Combine(AppContext.BaseDirectory,"Migrations","Delivery");
        await db.Execute(await File.ReadAllTextAsync(Path.Combine(root,"20261008_09_verify_motoboy_work.sql")));
        await db.Execute(await File.ReadAllTextAsync(Path.Combine(root,"20261008_11_verify_motoboy_work_periodo_parcial.sql")));
        // Rollback do período parcial/backfill é bloqueado com dado novo, liberado depois de limpo.
        var rollbackPeriodo=await File.ReadAllTextAsync(Path.Combine(root,"rollback","20261008_10_motoboy_work_periodo_parcial.sql"));
        var plan=await WorkPlan(db,"week",100m);
        await db.Execute("UPDATE delivery_rider_pay_plans SET created_at_utc='2026-01-05T00:00:00-03:00'::timestamptz");
        var service=new MotoboyWorkService(db.Source);
        await service.AddPeriod(db.StoreId,1,9,new(){OperationId=Guid.NewGuid(),PlanId=plan.Id,From=DateTimeOffset.Parse("2026-01-05T00:00:00-03:00"),To=DateTimeOffset.Parse("2026-01-06T00:00:00-03:00")},default);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>db.Execute(rollbackPeriodo));
        await db.Execute("DELETE FROM delivery_rider_work_entries");
        await db.Execute(rollbackPeriodo);
        Assert.False(await db.Scalar<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_name='delivery_rider_work_entries' AND column_name='period_total_seconds')"));
        await db.Execute(await File.ReadAllTextAsync(Path.Combine(root,"20261008_10_motoboy_work_periodo_parcial.sql")));
        // Rollback completo (tabelas inteiras) continua bloqueado com qualquer regra/lançamento vivo.
        var rollback=await File.ReadAllTextAsync(Path.Combine(root,"rollback","20261008_08_motoboy_work.sql"));
        await db.Execute("DELETE FROM delivery_rider_pay_plans");
        await db.Execute(rollback);
        Assert.False(await db.Scalar<bool>("SELECT to_regclass('delivery_rider_work_entries') IS NOT NULL"));
        await db.Execute(await File.ReadAllTextAsync(Path.Combine(root,"20261008_08_motoboy_work.sql")));
        await WorkPlan(db);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>db.Execute(rollback));
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_rider_pay_plans"));
    }
}
