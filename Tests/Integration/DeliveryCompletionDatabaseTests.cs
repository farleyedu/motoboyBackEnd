using APIBack.DTOs.Delivery;
using APIBack.Repository;
using APIBack.Service;
using Xunit;
namespace APIBack.Tests.Integration;
public partial class DeliverySyncDatabaseTests
{
    private static async Task<Database> CompletionDatabase()
    {
        var db=await Database.Create();
        await db.Execute("""
UPDATE motoboy SET canonical_motoboy_id=id;
CREATE TABLE delivery_motoboy_route(motoboy_id int,estabelecimento_id uuid,version bigint,updated_at_utc timestamptz,route_state text DEFAULT 'delivering',returning_since_utc timestamptz,PRIMARY KEY(motoboy_id,estabelecimento_id));
CREATE TABLE delivery_settings(estabelecimento_id uuid PRIMARY KEY,transfer_policy text DEFAULT 'direct',require_delivery_code bool DEFAULT true,allow_motoboy_reorder bool DEFAULT true,allow_motoboy_refuse bool DEFAULT true,updated_at_utc timestamptz DEFAULT NOW(),store_return_radius_m int DEFAULT 100,require_return_to_store bool DEFAULT false,auto_finish_route_on_return bool DEFAULT true,require_motoboy_acceptance bool DEFAULT false,offer_timeout_minutes int DEFAULT 5);
CREATE TABLE pedido(id int PRIMARY KEY,id_estabelecimento uuid,status_pedido int,motoboy_responsavel int,horario_saida text,horario_entrega text,nome_cliente text,telefone_cliente text,endereco_entrega text,entrega_rua text,entrega_numero text,entrega_bairro text,entrega_cidade text,entrega_estado text,entrega_cep text,latitude text,longitude text,items text,value text,tipo_pagamento text,status_pagamento text,troco text,observacoes text,previsao_entrega text,data_pedido text,codigo_entrega text);
CREATE TABLE delivery_route_stops(id bigint PRIMARY KEY,pedido_id int,motoboy_id int,estabelecimento_id uuid,position int,stop_status text,assigned_at_utc timestamptz,picked_up_at_utc timestamptz,arrived_at_utc timestamptz,locked bool DEFAULT false,offer_id uuid,offered_at_utc timestamptz,started_at_utc timestamptz,completed_at_utc timestamptz,completed_by text,updated_at_utc timestamptz);
CREATE TABLE delivery_transfer_requests(id bigint,pedido_id int,estabelecimento_id uuid,from_motoboy_id int,to_motoboy_id int,status text,decision_note text,decided_at_utc timestamptz,updated_at_utc timestamptz);
INSERT INTO delivery_settings(estabelecimento_id) VALUES(@StoreId);
INSERT INTO delivery_motoboy_route(motoboy_id,estabelecimento_id,version)VALUES(1,@StoreId,3);
INSERT INTO pedido(id,id_estabelecimento,status_pedido,motoboy_responsavel,nome_cliente,value,status_pagamento,codigo_entrega)VALUES(23,@StoreId,2,1,'Cliente','86.90','a_receber','4821'),(24,@StoreId,5,1,'Próximo','64.00','pago',NULL);
INSERT INTO delivery_route_stops(id,pedido_id,motoboy_id,estabelecimento_id,position,stop_status,assigned_at_utc,picked_up_at_utc,arrived_at_utc)VALUES(1,23,1,@StoreId,1,'en_route',NOW(),NOW(),NOW()),(2,24,1,@StoreId,2,'assigned',NOW(),NOW(),NULL);
""",new {db.StoreId});
        await db.Execute(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Migrations","Delivery","20261008_02_delivery_completion.sql")));
        return db;
    }
    private static DeliveryCompletionRequest CompletionRequest()=>new(){OperationId=Guid.NewGuid(),ExpectedPedidoId=23,ExpectedVersion=3,Codigo="4821",Payments=new(){new(){Method="pix",Amount=20m,ReceivedConfirmed=true},new(){Method="dinheiro",Amount=66.90m,CashReceived=100m,ReceivedConfirmed=true}}};
    private static string Photo()
    {
        using var bitmap=new SkiaSharp.SKBitmap(32,24);
        using var canvas=new SkiaSharp.SKCanvas(bitmap);canvas.Clear(SkiaSharp.SKColors.CornflowerBlue);
        using var image=SkiaSharp.SKImage.FromBitmap(bitmap);
        using var encoded=image.Encode(SkiaSharp.SKEncodedImageFormat.Png,100);
        return Convert.ToBase64String(encoded.ToArray());
    }
    [DeliveryDatabaseFact] public async Task PrivateProofIsReviewedAndOnlyReceiptOwnerCanReadAfterCompletion()
    {
        await using var db=await CompletionDatabase();var repo=new PedidoQueueRepository(db.Source);
        var proof=await repo.SaveCompletionProofAsync(db.StoreId,1,23,Photo(),default);
        Assert.StartsWith("data:image/jpeg;base64,",await repo.ReadPendingCompletionProofAsync(db.StoreId,1,23,default));
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.ReadPendingCompletionProofAsync(db.StoreId,2,23,default));
        Assert.Equal(proof,await repo.SaveCompletionProofAsync(db.StoreId,1,23,Photo(),default));
        var request=CompletionRequest();request.ProofId=proof;
        await repo.CompleteDeliveryAsync(db.StoreId,1,7,db.SessionId,4,request,default);
        Assert.NotNull(await repo.GetDeliveryProofAsync(7,request.OperationId,default));
        Assert.Null(await repo.GetDeliveryProofAsync(8,request.OperationId,default));
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.ReadPendingCompletionProofAsync(db.StoreId,1,23,default));
    }
    [DeliveryDatabaseFact] public async Task AccountUpdatesAndPrivateDocumentsOnlyAffectOwnCanonicalIdentity()
    {
        await using var db=await Database.Create();
        await db.Execute("""
CREATE TABLE usuario(id int PRIMARY KEY,nome text,email text,deleted_at timestamptz,updated_at timestamptz);
INSERT INTO usuario VALUES(7,'Teste','teste@example.com',NULL,NOW()),(8,'Outra pessoa','outra@example.com',NULL,NOW());
ALTER TABLE motoboy ADD COLUMN id_usuario int,ADD COLUMN telefone text,ADD COLUMN cidade text,ADD COLUMN uf text,ADD COLUMN modelo_moto text,ADD COLUMN placa_moto text,ADD COLUMN tipo_veiculo text,ADD COLUMN status_cadastro text DEFAULT 'pendente';
UPDATE motoboy SET id_usuario=7,canonical_motoboy_id=id;
INSERT INTO motoboy(id,nome,id_usuario,canonical_motoboy_id,is_simulated)VALUES(2,'Outra pessoa',8,2,false);
""");
        await db.Execute(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Migrations","Delivery","20261008_01_motoboy_conta_documentos.sql")));
        var service=new MotoboyContaService(db.Source);
        Assert.True(await service.UpdateDadosAsync(7,new(){Nome="Nome atualizado",Email="atualizado@example.com",Cidade="Campinas",Uf="sp"},default));
        Assert.True(await service.UpdateVeiculoAsync(7,new(){ModeloMoto="Honda CG",PlacaMoto="ABC1D23",AnoMoto=2024},default));
        var doc=await service.SaveImageAsync(7,new(){Tipo="cnh",Base64=Photo()},default);
        Assert.NotNull(doc);Assert.NotNull(await service.ReadImageAsync(7,doc.Id,default));Assert.Null(await service.ReadImageAsync(8,doc.Id,default));
        Assert.Empty(await service.DocumentsAsync(8,default));Assert.Equal("recebido",(await service.DocumentsAsync(7,default)).Single().Status);
        Assert.Equal("Outra pessoa",(await service.GetAsync(8,default))?.Nome);Assert.Equal("pendente",(await service.GetAsync(7,default))?.StatusCadastro);
        Assert.False(await service.UpdateDadosAsync(999,new(){Nome="Intruso",Email="invasor@example.com"},default));
    }
    [DeliveryDatabaseFact] public async Task CompletionCommitsPaymentReceiptAndNextExactlyOnce()
    {
        await using var db=await CompletionDatabase();var repo=new PedidoQueueRepository(db.Source);var request=CompletionRequest();
        var result=await repo.CompleteDeliveryAsync(db.StoreId,1,7,db.SessionId,4,request,default);
        Assert.Equal(24,result.Queue.Current?.PedidoId);Assert.Equal(23,result.Receipt.PedidoId);Assert.Equal("pago",await db.Scalar<string>("SELECT status_pagamento FROM pedido WHERE id=23"));
        var replay=await repo.CompleteDeliveryAsync(db.StoreId,1,7,db.SessionId,4,request,default);
        Assert.Equal(result.Receipt.CompletedAtUtc,replay.Receipt.CompletedAtUtc);Assert.Equal(result.Queue.Version,replay.Queue.Version);Assert.Equal(24,replay.Queue.Current?.PedidoId);
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_completions"));Assert.Equal(2,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox")); // Um evento para loja e outro para o motoboy.
        Assert.Null(await repo.GetDeliveryReceiptAsync(8,request.OperationId,default));Assert.NotNull(await repo.GetDeliveryReceiptAsync(7,request.OperationId,default));
        request.Payments[0].Amount=21m;Assert.Equal("IDEMPOTENCY_CONFLICT",(await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.CompleteDeliveryAsync(db.StoreId,1,7,db.SessionId,4,request,default))).Code);
    }
    [DeliveryDatabaseFact] public async Task ParallelRetriesOnlyCompleteOriginalOrder()
    {
        await using var db=await CompletionDatabase();var repo=new PedidoQueueRepository(db.Source);var request=CompletionRequest();
        var results=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>repo.CompleteDeliveryAsync(db.StoreId,1,7,db.SessionId,4,request,default)));
        Assert.All(results,r=>Assert.Equal(24,r.Queue.Current?.PedidoId));Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_completions"));
    }
    [DeliveryDatabaseFact] public async Task InvalidCodeOrPaymentRollsBackEverything()
    {
        await using var db=await CompletionDatabase();var repo=new PedidoQueueRepository(db.Source);var request=CompletionRequest();request.Codigo="0000";
        Assert.Equal("DELIVERY_CODE_INVALID",(await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.CompleteDeliveryAsync(db.StoreId,1,7,db.SessionId,4,request,default))).Code);
        request.Codigo="4821";request.Payments[0].ReceivedConfirmed=false;
        Assert.Equal("PAYMENT_NOT_CONFIRMED",(await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.CompleteDeliveryAsync(db.StoreId,1,7,db.SessionId,4,request,default))).Code);
        Assert.Equal("a_receber",await db.Scalar<string>("SELECT status_pagamento FROM pedido WHERE id=23"));Assert.Equal(0,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_completions"));Assert.Equal(0,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox"));
    }
    [DeliveryDatabaseFact] public async Task MilestonesVersionTenantAndSessionAreEnforced()
    {
        await using var db=await CompletionDatabase();var repo=new PedidoQueueRepository(db.Source);var r=CompletionRequest();r.ExpectedVersion=2;
        Assert.Equal("QUEUE_VERSION_CONFLICT",(await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.CompleteDeliveryAsync(db.StoreId,1,7,db.SessionId,4,r,default))).Code);
        r.ExpectedVersion=3;r.ExpectedPedidoId=24;Assert.Equal("CURRENT_DELIVERY_CHANGED",(await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.CompleteDeliveryAsync(db.StoreId,1,7,db.SessionId,4,r,default))).Code);
        r.ExpectedPedidoId=23;Assert.Equal("SESSION_CHANGED",(await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.CompleteDeliveryAsync(db.StoreId,1,8,db.SessionId,4,r,default))).Code);
        await db.Execute("UPDATE delivery_route_stops SET arrived_at_utc=NULL WHERE id=1");Assert.Equal("DELIVERY_MILESTONES_REQUIRED",(await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.CompleteDeliveryAsync(db.StoreId,1,7,db.SessionId,4,r,default))).Code);
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.GetCompletionContextAsync(Guid.NewGuid(),1,23,default));
    }
    [DeliveryDatabaseFact] public async Task CodeCheckDoesNotCompleteOrChargeAndProofIsBoundToStop()
    {
        await using var db=await CompletionDatabase();var repo=new PedidoQueueRepository(db.Source);var r=CompletionRequest();
        Assert.True(await repo.ValidateCompletionCodeAsync(db.StoreId,1,23,"4821",default));Assert.Equal("a_receber",await db.Scalar<string>("SELECT status_pagamento FROM pedido WHERE id=23"));
        await db.Execute("UPDATE delivery_settings SET require_delivery_proof=true");Assert.True((await repo.GetCompletionContextAsync(db.StoreId,1,23,default)).RequiresProof);
        Assert.Equal("PROOF_REQUIRED",(await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.CompleteDeliveryAsync(db.StoreId,1,7,db.SessionId,4,r,default))).Code);
        r.ProofId=Guid.NewGuid();Assert.Equal("PROOF_INVALID",(await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.CompleteDeliveryAsync(db.StoreId,1,7,db.SessionId,4,r,default))).Code);
    }
    [DeliveryDatabaseFact] public async Task PauseDoesNotEndSessionOrUnassignAndPickupIsAtomic()
    {
        await using var db=await CompletionDatabase();var repo=new PedidoQueueRepository(db.Source);
        var paused=await repo.PauseTurnAsync(db.StoreId,1,db.SessionId,4,true);Assert.True(paused.Paused);Assert.Equal(23,paused.Current?.PedidoId);Assert.Equal(24,paused.Next.Single().PedidoId);
        await db.Execute("UPDATE delivery_route_stops SET picked_up_at_utc=NULL,arrived_at_utc=NULL");
        var r=new PickupStopsRequest{ExpectedPedidoId=23,ExpectedVersion=paused.Version,PedidoIds=new(){23,24}};
        var picked=await repo.PickUpStopsAsync(db.StoreId,1,r);Assert.NotNull(picked.Current?.PickedUpAtUtc);Assert.NotNull(picked.Next.Single().PickedUpAtUtc);
        Assert.Equal(picked.Version,(await repo.PickUpStopsAsync(db.StoreId,1,r)).Version);
        await db.Execute("INSERT INTO pedido(id,id_estabelecimento,status_pedido)VALUES(25,@StoreId,1)",new {db.StoreId});
        Assert.Equal("MOTOBOY_PAUSED",(await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.AssignAsync(db.StoreId,7,1,25))).Code);
    }
    [DeliveryDatabaseFact] public async Task OccurrenceCannotRemoveAnotherCurrentOrder()
    {
        await using var db=await CompletionDatabase();var repo=new PedidoQueueRepository(db.Source);
        Assert.Equal("CURRENT_DELIVERY_CHANGED",(await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.FailCurrentForPedidoAsync(db.StoreId,1,24,"Cliente não localizado"))).Code);
        Assert.Equal("en_route",await db.Scalar<string>("SELECT stop_status FROM delivery_route_stops WHERE pedido_id=23"));
        Assert.Equal("a_receber",await db.Scalar<string>("SELECT status_pagamento FROM pedido WHERE id=23"));
    }
    [DeliveryDatabaseFact] public async Task MigrationsReapplyVerifyAndRollbackPreservesEvidenceAndAdditiveColumns()
    {
        await using var db=await CompletionDatabase();var path=Path.Combine(AppContext.BaseDirectory,"Migrations","Delivery");
        await db.Execute(await File.ReadAllTextAsync(Path.Combine(path,"20261008_01_motoboy_conta_documentos.sql")));
        await db.Execute(await File.ReadAllTextAsync(Path.Combine(path,"20261008_01_motoboy_conta_documentos.sql")));
        await db.Execute(await File.ReadAllTextAsync(Path.Combine(path,"20261008_02_delivery_completion.sql")));
        await db.Execute(await File.ReadAllTextAsync(Path.Combine(path,"20261008_03_verify.sql")));
        var rollback=await File.ReadAllTextAsync(Path.Combine(path,"rollback","20261008_01_02_conta_completion.down.sql"));
        var repo=new PedidoQueueRepository(db.Source);await repo.SaveCompletionProofAsync(db.StoreId,1,23,Photo(),default);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>db.Execute(rollback));Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_completion_proofs"));
        await db.Execute("DELETE FROM delivery_completion_proofs");await db.Execute(rollback);
        Assert.Null(await db.Scalar<string?>("SELECT to_regclass('delivery_completions')::text"));
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM information_schema.columns WHERE table_schema=current_schema() AND table_name='motoboy' AND column_name='ano_moto'"));
    }
}
