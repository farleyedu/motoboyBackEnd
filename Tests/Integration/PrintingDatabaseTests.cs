using APIBack.DTOs.Delivery;
using APIBack.Printing;
using APIBack.Repository.Interface;
using Dapper;
using Moq;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;
public sealed class PrintingDatabaseTests
{
 [DeliveryDatabaseFact] public async Task ManualRulesRespectOriginCopiesAndReplayDoesNotCreateAnotherReceipt()
 {
  await using var f=await Fixture.Create();var config=await f.Configure();config.Regras[0].Gatilho="manual";config.Regras[0].Origens=["atendente"];config.Regras[0].Vias=2;config.Automatica=false;await f.Store.Save(f.StoreId,config);
  var key=Guid.NewGuid().ToString();await f.Store.Manual(f.StoreId,1,42,null,false,key);await f.Store.Manual(f.StoreId,1,42,null,false,key);Assert.Equal(2,(await f.Store.Jobs(f.StoreId)).Count);
  f.Orders.Setup(o=>o.GetAsync(f.StoreId,43)).ReturnsAsync(new PedidoDetalheDto{Id=43,Origem="cardapio_web"});
  await Assert.ThrowsAsync<APIBack.Service.DeliveryDomainException>(()=>f.Store.Manual(f.StoreId,1,43,null,false,Guid.NewGuid().ToString()));
 }
 [DeliveryDatabaseFact] public async Task DelayedDispatcherDoesNotRenewTheOriginalAcceptanceDeadline()
 {
  await using var f=await Fixture.Create();await f.Configure();
  await using(var db=await f.Source.OpenConnectionAsync())await db.ExecuteAsync("INSERT INTO pedido VALUES(42,@store,'atendente',1); UPDATE impressao_aceite SET criado_em=now()-interval '1 hour'",new{store=f.StoreId});
  Assert.True(await new PrintDispatcher(f.Source,f.Store,f.Orders.Object).DispatchNext(default));var job=Assert.Single(await f.Store.Jobs(f.StoreId));Assert.Equal("expirado",job.Estado);Assert.Contains("prazo",job.Motivo);
 }
 [DeliveryDatabaseFact] public async Task CommittedAcceptanceDispatchesOnceAcrossTwoWorkersAndRollbacksDoNotPrint()
 {
  await using var f=await Fixture.Create();var config=await f.Configure();
  await using(var db=await f.Source.OpenConnectionAsync())await db.ExecuteAsync("INSERT INTO pedido VALUES(42,@store,'atendente',1)",new{store=f.StoreId});
  var dispatcher=new PrintDispatcher(f.Source,f.Store,f.Orders.Object);
  await Task.WhenAll(dispatcher.DispatchNext(default),dispatcher.DispatchNext(default));
  Assert.Single(await f.Store.Jobs(f.StoreId));Assert.Equal("pendente",(await f.Store.Jobs(f.StoreId))[0].Estado);
  await using(var db=await f.Source.OpenConnectionAsync())
  {await using var tx=await db.BeginTransactionAsync();await db.ExecuteAsync("INSERT INTO pedido VALUES(43,@store,'atendente',1)",new{store=f.StoreId},tx);await tx.RollbackAsync();}
  Assert.False(await dispatcher.DispatchNext(default));
  // Replayed/parallel acceptance has a unique key and never creates another job.
  await using(var db=await f.Source.OpenConnectionAsync())await db.ExecuteAsync("UPDATE impressao_aceite SET processado_em=NULL WHERE pedido_id=42");
  Assert.True(await dispatcher.DispatchNext(default));Assert.Single(await f.Store.Jobs(f.StoreId));
 }
 [DeliveryDatabaseFact] public async Task TwoBrowserTabsCannotClaimTheSameJobAndExpiredJobsCannotBeDelivered()
 {
  await using var f=await Fixture.Create();var config=await f.Configure();var station=config.Impressoras[0].EstacaoId;var first=Guid.NewGuid();var second=Guid.NewGuid();
  Assert.True(await f.Store.Signal(f.StoreId,station,1,new(first,null,null,"Chrome",null)));
  Assert.False(await f.Store.Signal(f.StoreId,station,2,new(second,null,null,"Chrome",null)));
  await f.Store.Manual(f.StoreId,1,42,null,false,Guid.NewGuid().ToString());var job=(await f.Store.Jobs(f.StoreId))[0];
  var claims=await Task.WhenAll(f.Store.Receive(f.StoreId,station,job.Id,first,false),f.Store.Receive(f.StoreId,station,job.Id,first,false));Assert.Single(claims.Where(v=>v));
  Assert.False(await f.Store.Receive(f.StoreId,station,job.Id,second,false));
  await Assert.ThrowsAsync<APIBack.Service.DeliveryDomainException>(()=>f.Store.Result(f.StoreId,station,job.Id,new("enviado",null,second),false));
  await f.Store.Result(f.StoreId,station,job.Id,new("enviado",null,first),false);
  await f.Store.Result(f.StoreId,station,job.Id,new("enviado",null,first),false); // idempotent ack
  await using(var db=await f.Source.OpenConnectionAsync())await db.ExecuteAsync("UPDATE impressao_job SET estado='expirado' WHERE id=@id",new{id=job.Id});
  await f.Store.Result(f.StoreId,station,job.Id,new("enviado",null,first),false); // confirmation after an outage must not block the station
  Assert.Equal("enviado",(await f.Store.Jobs(f.StoreId))[0].Estado);
  await f.Store.Manual(f.StoreId,1,42,null,false,Guid.NewGuid().ToString());job=(await f.Store.Jobs(f.StoreId))[0];
  await using(var db=await f.Source.OpenConnectionAsync())await db.ExecuteAsync("UPDATE impressao_job SET expira_em=now()-interval '1 second' WHERE id=@id",new{id=job.Id});
  Assert.False(await f.Store.Receive(f.StoreId,station,job.Id,first,false));
 }
 [DeliveryDatabaseFact] public async Task PairingIsSingleUseTokensAreHashedAndRevocationIsImmediate()
 {
  await using var f=await Fixture.Create();await f.Store.Config(f.StoreId,true);var station=await f.Store.CreateStation(f.StoreId,1,new("Agente","agente"));
  var code=System.Text.Json.JsonSerializer.SerializeToElement(await f.Store.PairCode(f.StoreId,1,station.Id),PrintStore.Json).GetProperty("codigo").GetString()!;
  var response=System.Text.Json.JsonSerializer.SerializeToElement(await f.Store.Pair(new(code,"PC TESTE")),PrintStore.Json);var token=response.GetProperty("token").GetString()!;
  Assert.NotNull(await f.Store.Agent(token));
  await using(var db=await f.Source.OpenConnectionAsync())Assert.NotEqual(token,await db.ExecuteScalarAsync<string>("SELECT token_hash FROM impressao_estacao WHERE id=@id",new{id=station.Id}));
  await Assert.ThrowsAsync<APIBack.Service.DeliveryDomainException>(()=>f.Store.Pair(new(code,"Outro PC")));
  Assert.Empty(await f.Store.Stations(Guid.NewGuid()));
  await f.Store.SetStation(f.StoreId,station.Id,null);Assert.Null(await f.Store.Agent(token));
 }
 [DeliveryDatabaseFact] public async Task DraftDoesNotChangeActiveConfigurationAndOtherStoreIdsAreRejected()
 {
  await using var f=await Fixture.Create();var config=await f.Configure();var draft=new PrintDraft("modelos",System.Text.Json.JsonSerializer.SerializeToElement(new{config=new{automatica=false}},PrintStore.Json));
  await f.Store.SaveDraft(f.StoreId,1,draft);Assert.True((await f.Store.Config(f.StoreId)).Automatica);Assert.NotNull(await f.Store.Draft(f.StoreId,1));Assert.Null(await f.Store.Draft(f.StoreId,2));
  var other=Guid.NewGuid();await f.AddShop(other);var otherConfig=await f.Store.Config(other,true);
  otherConfig.Impressoras=config.Impressoras;
  await Assert.ThrowsAsync<APIBack.Service.DeliveryDomainException>(()=>f.Store.Save(other,otherConfig));
  var stale=await f.Store.Config(f.StoreId);await f.Store.Save(f.StoreId,config);
  await Assert.ThrowsAsync<APIBack.Service.DeliveryDomainException>(()=>f.Store.Save(f.StoreId,stale));
 }
 [DeliveryDatabaseFact] public async Task IfoodOnlyDispatchesAfterOfficialConfirmationAndSimulatorNeverCreatesAcceptance()
 {
  await using var f=await Fixture.Create();await f.Configure();var merchant=Guid.NewGuid();var external=Guid.NewGuid();
  await using var db=await f.Source.OpenConnectionAsync();
  await db.ExecuteAsync("INSERT INTO pedido VALUES(42,@store,'ifood',6); INSERT INTO pedido VALUES(44,@store,'simulador',1); INSERT INTO ifood_order_links VALUES(@merchant,@external,@store,42); INSERT INTO ifood_order_snapshots VALUES(@merchant,@external,@store,'PLACED')",new{store=f.StoreId,merchant,external});
  Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT count(*) FROM impressao_aceite"));
  await db.ExecuteAsync("UPDATE ifood_order_snapshots SET remote_status='CONFIRMED'");Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT count(*) FROM impressao_aceite"));
 }
 private sealed class Fixture:IAsyncDisposable
 {
  public NpgsqlDataSource Source {get;private set;}=null!;
  public PrintStore Store {get;private set;}=null!;
  public Guid StoreId {get;}=Guid.NewGuid();
  public Mock<IPedidoConsultaRepository> Orders {get;}=new();
  private string schema="print_"+Guid.NewGuid().ToString("N");private string connection="";
  public static async Task<Fixture> Create()
  {
   var f=new Fixture();var builder=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE"));
   if(builder.Host is not("127.0.0.1" or "localhost" or "::1"))throw new InvalidOperationException("Teste de impressão exige PostgreSQL local.");
   f.connection=builder.ConnectionString;
   await using(var db=new NpgsqlConnection(f.connection)){await db.OpenAsync();await db.ExecuteAsync($"CREATE SCHEMA {f.schema}");}
   builder.SearchPath=f.schema;builder.Pooling=false;f.Source=NpgsqlDataSource.Create(builder.ConnectionString);
   await using(var db=await f.Source.OpenConnectionAsync())
   {
    await db.ExecuteAsync("""
CREATE TABLE estabelecimentos(id uuid PRIMARY KEY,nome_fantasia text,cnpj_loja text,telefone text,email text,logradouro text,numero text,bairro text,cidade text,uf text,url_logo text,site_url text,instagram_url text,facebook_url text);
CREATE TABLE pedido(id int PRIMARY KEY,id_estabelecimento uuid,origem text,status_pedido int);
CREATE TABLE ifood_order_links(merchant_id uuid,external_order_id uuid,estabelecimento_id uuid,pedido_id int);
CREATE TABLE ifood_order_snapshots(merchant_id uuid,external_order_id uuid,estabelecimento_id uuid,remote_status text);
CREATE TABLE delivery_tracking_schema_versions(version text PRIMARY KEY);
CREATE TABLE delivery_realtime_outbox(event_id uuid,event_name text,target_group text,estabelecimento_id uuid,aggregate_version bigint,payload jsonb,occurred_at_utc timestamptz);
""");
    var path=Path.Combine(AppContext.BaseDirectory,"Migrations","Delivery","20261019_01_impressao.sql");await db.ExecuteAsync(await File.ReadAllTextAsync(path));await db.ExecuteAsync(await File.ReadAllTextAsync(path));
   }
   await f.AddShop(f.StoreId);
   f.Orders.Setup(o=>o.GetAsync(f.StoreId,It.IsAny<int>())).ReturnsAsync((Guid _,int id)=>new PedidoDetalheDto{Id=id,Origem="atendente",Total=20,NomeCliente="Teste",Itens=[new(){Nome="Café",Quantidade=1,Total=20}]});
   f.Store=new(f.Source,f.Orders.Object);return f;
  }
  public async Task AddShop(Guid id){await using var db=await Source.OpenConnectionAsync();await db.ExecuteAsync("INSERT INTO estabelecimentos(id,nome_fantasia) VALUES(@id,'Loja de teste')",new{id});}
  public async Task<PrintConfig> Configure()
  {
   var config=await Store.Config(StoreId,true);var station=await Store.CreateStation(StoreId,1,new("Caixa","navegador"));var printer=new PrintPrinter{EstacaoId=station.Id};config.Impressoras.Add(printer);config.Regras[0].ImpressoraId=printer.Id;config.Automatica=true;return await Store.Save(StoreId,config);
  }
  public async ValueTask DisposeAsync()
  {await Source.DisposeAsync();await using var db=new NpgsqlConnection(connection);await db.OpenAsync();await db.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");}
 }
}
