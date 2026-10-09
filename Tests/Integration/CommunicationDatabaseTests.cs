using APIBack.DTOs.Atendimento;
using APIBack.Infrastructure;
using APIBack.Repository;
using APIBack.Service;
using Dapper;
using Npgsql;
using Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace APIBack.Tests.Integration;

public sealed class CommunicationDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task MigrationLegacyInteropIdempotencyPagingMentionsReadAndPrivateIsolation()
    {
        await using var db=await Fixture.Create();var repo=new CommunicationRepository(db.Source);
        var alice=new ChatActor(db.Est,7,1);var bob=new ChatActor(db.Est,8,2);var admin=new ChatActor(db.Est,9,null);
        var store=new ChatThread("store","store:1",1);var group=new ChatThread("group","group",null);var priv=new ChatThread("private","private:1:2",2);
        Assert.Single((await repo.ListAsync(alice,store,null,null,50,null)).Messages);
        var request=new SendCommunicationRequest{ClientId=Guid.NewGuid(),Body="@Bob atencao",Mentions=new[]{2}};
        var sent=await repo.SendAsync(alice,group,request,"Alice","hash");
        var again=await repo.SendAsync(alice,group,request,"Alice","hash");Assert.Equal(sent.Id,again.Id);
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM motoboy_group_message"));
        Assert.Equal("CHAT_ID_CONFLICT",(await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.SendAsync(alice,group,request,"Alice","other"))).Code);
        var notifications=await repo.NotificationsAsync(bob);Assert.True(Assert.Single(notifications).Mentioned);
        await repo.DismissAsync(bob,sent.Id);Assert.Empty(await repo.NotificationsAsync(bob));
        await repo.ReadAsync(bob,group,sent.Sequence);
        await repo.ReactAsync(bob,group,sent.Id,"thanks");
        var read=Assert.Single((await repo.ListAsync(alice,group,null,null,50,null)).Messages);Assert.Equal(1,read.ReadCount);Assert.Equal("thanks",Assert.Single(read.Reactions).Reaction);
        var reply=await repo.SendAsync(bob,group,new(){ClientId=Guid.NewGuid(),Body="Obrigado",ReplyTo=sent.Id},"Bob","reply");Assert.Equal(sent.Body,reply.ReplyBody);
        var page=await repo.ListAsync(alice,group,null,null,1,null);Assert.True(page.HasMore);Assert.Equal(reply.Id,Assert.Single(page.Messages).Id);
        Assert.Equal(sent.Id,Assert.Single((await repo.ListAsync(alice,group,page.Cursor,null,1,null)).Messages).Id);
        Assert.Single((await repo.ListAsync(alice,group,null,"atencao",50,null)).Messages);
        var privateMessage=await repo.SendAsync(alice,priv,new(){ClientId=Guid.NewGuid(),Body="So entre nos"},"Alice","private");
        Assert.Equal("CHAT_REPLY_INVALID",(await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.SendAsync(alice,group,new(){ClientId=Guid.NewGuid(),Body="vazamento",ReplyTo=privateMessage.Id},"Alice","bad"))).Code);
        Assert.Equal(0,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox WHERE payload->>'channel'='private' AND target_group LIKE 'establishment:%'"));
        await db.Execute("UPDATE motoboy_estabelecimento SET ativo=FALSE WHERE motoboy_id=2");
        Assert.Equal("CHAT_LINK_FORBIDDEN",(await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.SendAsync(alice,priv,new(){ClientId=Guid.NewGuid(),Body="bloqueada"},"Alice","blocked"))).Code);
        await db.Migration("20261008_05_verify_comunicacao.sql");
    }
    [DeliveryDatabaseFact]
    public async Task ProtectedAttachmentsAndReadCutoffDoNotMarkFutureMessages()
    {
        await using var db=await Fixture.Create();var repo=new CommunicationRepository(db.Source);var actor=new ChatActor(db.Est,7,1);var admin=new ChatActor(db.Est,9,null);var thread=new ChatThread("store","store:1",1);
        var file=await repo.SaveFileAsync(actor,thread,"foto.png","image/png",new byte[]{1,2,3});
        Assert.Null(await repo.FileAsync(Guid.NewGuid(),file.Id));Assert.False(await repo.FilePublishedAsync(db.Est,file.Id));
        var sent=await repo.SendAsync(actor,thread,new(){ClientId=Guid.NewGuid(),Body="",AttachmentId=file.Id},"Alice","file");Assert.Equal(file.Id,sent.Attachment!.Id);
        var future=await repo.SendAsync(actor,thread,new(){ClientId=Guid.NewGuid(),Body="futura"},"Alice","future");
        await repo.ReadAsync(admin,thread,sent.Sequence);
        var list=(await repo.ListAsync(actor,thread,null,null,50,null)).Messages;
        Assert.Equal(1,list.Single(m=>m.Id==sent.Id).ReadCount);Assert.Equal(0,list.Single(m=>m.Id==future.Id).ReadCount);
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_motoboy_message WHERE direction='motoboy' AND read_at_utc IS NOT NULL"));
        var other=new ChatActor(db.Est,8,2);var otherThread=new ChatThread("store","store:2",2);
        Assert.Equal("CHAT_ATTACHMENT_INVALID",(await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.SendAsync(other,otherThread,new(){ClientId=Guid.NewGuid(),AttachmentId=file.Id},"Bob","bad"))).Code);
    }
    [DeliveryDatabaseFact]
    public async Task ClientDispatchDoesNotSendAgainAfterLostExternalConfirmation()
    {
        await using var db=await Fixture.Create();var repo=new ClientCommunicationRepository(db.Source);var actor=new ChatActor(db.Est,7,1);var request=new SendCommunicationRequest{ClientId=Guid.NewGuid(),Body="Oi"};
        Assert.Null(await repo.ClaimAsync(actor,23,request,"hash"));
        Assert.Equal("CHAT_SEND_UNCERTAIN",(await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.ClaimAsync(actor,23,request,"hash"))).Code);
        var id=Guid.NewGuid();await repo.FinishAsync(actor,request.ClientId,id);Assert.Equal(id,(await repo.ClaimAsync(actor,23,request,"hash"))!.MessageId);
        Assert.Equal("CHAT_ID_CONFLICT",(await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.ClaimAsync(actor,23,request,"changed"))).Code);
    }
    [DeliveryDatabaseFact]
    public async Task CommunicationPushDoesNotFollowTokenToAnotherUserOrTurn()
    {
        await using var db=await Fixture.Create();var repo=new CommunicationRepository(db.Source);var register=new ChatPushService(db.Source);
        var alice=new ChatActor(db.Est,7,1);var bob=new ChatActor(db.Est,8,2);
        var aliceSession=await db.Scalar<Guid>("SELECT session_id FROM motoboy_active_sessions WHERE motoboy_id=1");
        var bobSession=await db.Scalar<Guid>("SELECT session_id FROM motoboy_active_sessions WHERE motoboy_id=2");
        var token="ExpoPushToken[communication-test-token]";
        await register.RegisterAsync(bob,bobSession,new(){Token=token,Sound=true,Vibration=true});
        await repo.SendAsync(alice,new("private","private:1:2",2),new(){ClientId=Guid.NewGuid(),Body="Privada"},"Alice","private");
        var worker=new ChatPushWorker(db.Source,Mock.Of<IHttpClientFactory>(),new ConfigurationBuilder().Build(),NullLogger<ChatPushWorker>.Instance);
        var claimed=Assert.Single(await worker.ClaimAsync(default));Assert.Equal(bobSession,claimed.SessionId);Assert.True(await worker.CanPublishAsync(claimed.Id));
        await register.RegisterAsync(alice,aliceSession,new(){Token=token});
        Assert.False(await worker.CanPublishAsync(claimed.Id));Assert.Empty(await worker.ClaimAsync(default));
        Assert.Equal("dismissed",await db.Scalar<string>("SELECT state FROM delivery_chat_push_outbox"));
        await register.RegisterAsync(bob,bobSession,new(){Token=token});
        await repo.SendAsync(alice,new("group","group",null),new(){ClientId=Guid.NewGuid(),Body="@Bob",Mentions=new[]{2}},"Alice","mention");
        var nextSession=Guid.NewGuid();await db.Execute("INSERT INTO motoboy_active_sessions VALUES(@Session,2,@Est,NULL,NOW()+interval '1 hour');",new{Session=nextSession,db.Est});
        await register.RegisterAsync(bob,nextSession,new(){Token=token});Assert.Empty(await worker.ClaimAsync(default));
    }
    [DeliveryDatabaseFact]
    public async Task CommunicationMessageContextCannotCrossThreadOrEstablishment()
    {
        await using var db=await Fixture.Create();var repo=new CommunicationRepository(db.Source);var actor=new ChatActor(db.Est,7,1);var group=new ChatThread("group","group",null);
        var old=await repo.SendAsync(actor,group,new(){ClientId=Guid.NewGuid(),Body="Antiga"},"Alice","old");
        for(var i=0;i<60;i++)await repo.SendAsync(actor,group,new(){ClientId=Guid.NewGuid(),Body=$"Nova {i}"},"Alice",$"new-{i}");
        Assert.DoesNotContain((await repo.ListAsync(actor,group,null,null,50,null)).Messages,m=>m.Id==old.Id);
        Assert.Contains((await repo.ContextAsync(actor,group,old.Id,null)).Messages,m=>m.Id==old.Id);
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.ContextAsync(actor,new("store","store:1",1),old.Id,null));
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.ContextAsync(new(Guid.NewGuid(),7,1),group,old.Id,null));
    }
    [DeliveryDatabaseFact]
    public async Task CommunicationClientHistorySearchMediaReactionsAndCursorStayInTenant()
    {
        await using var db=await Fixture.Create();var repo=new ClientCommunicationRepository(db.Source);var client=Guid.NewGuid();var conversation=Guid.NewGuid();var other=Guid.NewGuid();var first=Guid.NewGuid();var second=Guid.NewGuid();
        await db.Execute(@"CREATE TABLE conversas(id uuid PRIMARY KEY,id_cliente uuid,id_estabelecimento uuid);
 CREATE TABLE mensagens(id uuid PRIMARY KEY,id_conversa uuid,conteudo text,tipo text,status text,criada_por text,id_provedor text,data_criacao timestamptz);
 CREATE TABLE wa_evento(tipo text,chave text,payload jsonb,phone_number_id text,recebido_em timestamptz DEFAULT NOW(),UNIQUE(tipo,chave));
 INSERT INTO conversas VALUES(@Conversation,@Client,@Est),(@Other,@Client,@OtherEst);
 INSERT INTO mensagens VALUES(@First,@Conversation,'foto alvo','imagem','entregue','cliente','wamid.one','2026-10-08T10:00:00Z'),
 (@Second,@Conversation,'outra','texto','entregue','cliente','wamid.two','2026-10-08T10:00:00Z'),(gen_random_uuid(),@Other,'foto alvo privada','texto','entregue','cliente','wamid.other',NOW());
 INSERT INTO wa_evento(tipo,chave,payload,phone_number_id) VALUES('mensagem','wamid.one','{""message"":{""type"":""image"",""image"":{""id"":""123456"",""mime_type"":""image/png""}}}','111');",
            new{Conversation=conversation,Client=client,db.Est,Other=other,OtherEst=Guid.NewGuid(),First=first,Second=second});
        var page=await repo.HistoryAsync(db.Est,conversation,"Motoboy",null,Guid.Empty,null,1);Assert.True(page.More);var latest=Assert.Single(page.Messages);
        var older=await repo.HistoryAsync(db.Est,conversation,"Motoboy",DateTime.SpecifyKind(latest.CreatedAtUtc,DateTimeKind.Utc),latest.Id,null,1);
        Assert.Single(older.Messages);Assert.NotEqual(latest.Id,older.Messages[0].Id);
        var found=Assert.Single((await repo.HistoryAsync(db.Est,conversation,"Motoboy",null,Guid.Empty,"alvo",50)).Messages);Assert.Equal(first,found.Id);
        Assert.Equal(23,ClientCommunicationService.IncomingAttachment(found,23)!.ClientPedidoId);
        Assert.Null(await repo.MessageAsync(Guid.NewGuid(),conversation,"",first));
        var actor=new ChatActor(db.Est,7,1);await repo.SetReactionAsync(actor,first,"like");Assert.True(Assert.Single((await repo.ReactionsAsync(actor,new[]{first}))[first]).Mine);
        Assert.Empty(await repo.ReactionsAsync(new(Guid.NewGuid(),7,1),new[]{first}));await repo.SetReactionAsync(actor,first,null);Assert.Empty(await repo.ReactionsAsync(actor,new[]{first}));
    }
    private sealed class Fixture:IAsyncDisposable
    {
        public Guid Est{get;}=Guid.NewGuid();public NpgsqlDataSource Source{get;private set;}=null!;
        private readonly string schema="chat_test_"+Guid.NewGuid().ToString("N");private string connection="";
        public static async Task<Fixture> Create()
        {
            var options=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE"));
            if(options.Host is not ("127.0.0.1" or "localhost"))throw new InvalidOperationException("Somente banco local de teste.");
            var fixture=new Fixture{connection=options.ConnectionString};
            await using(var admin=new NpgsqlConnection(fixture.connection)){await admin.OpenAsync();await admin.ExecuteAsync($"CREATE SCHEMA {fixture.schema}");}
            options.SearchPath=fixture.schema;fixture.Source=NpgsqlDataSource.Create(options.ConnectionString);SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
            await fixture.Execute(@"CREATE TABLE estabelecimentos(id uuid PRIMARY KEY);CREATE TABLE motoboy(id integer PRIMARY KEY,nome text);CREATE TABLE pedido(id integer PRIMARY KEY,id_estabelecimento uuid);
CREATE TABLE motoboy_estabelecimento(motoboy_id integer,estabelecimento_id uuid,ativo boolean);
CREATE TABLE motoboy_active_sessions(session_id uuid,motoboy_id integer,id_estabelecimento uuid,ended_at_utc timestamptz,expires_at_utc timestamptz);
CREATE TABLE delivery_tracking_schema_versions(version text PRIMARY KEY);
CREATE TABLE delivery_motoboy_message(id bigserial PRIMARY KEY,estabelecimento_id uuid,motoboy_id integer,pedido_id integer,direction text,body text,quick_key text,sent_by_user_id integer,created_at_utc timestamptz DEFAULT NOW(),read_at_utc timestamptz);
CREATE TABLE motoboy_group_message(id bigserial PRIMARY KEY,estabelecimento_id uuid,sender_type text,motoboy_id integer,sent_by_user_id integer,body text,created_at_utc timestamptz DEFAULT NOW());
CREATE TABLE delivery_realtime_outbox(event_id uuid,event_name text,target_group text,estabelecimento_id uuid,motoboy_id integer,aggregate_version bigint,payload jsonb,occurred_at_utc timestamptz);");
            await fixture.Execute("INSERT INTO estabelecimentos VALUES(@Est);INSERT INTO motoboy VALUES(1,'Alice'),(2,'Bob');INSERT INTO pedido VALUES(23,@Est);INSERT INTO motoboy_estabelecimento VALUES(1,@Est,TRUE),(2,@Est,TRUE);INSERT INTO motoboy_active_sessions VALUES(gen_random_uuid(),1,@Est,NULL,NOW()+interval '1 hour'),(gen_random_uuid(),2,@Est,NULL,NOW()+interval '1 hour');INSERT INTO delivery_motoboy_message(estabelecimento_id,motoboy_id,direction,body) VALUES(@Est,1,'operator','Historico antigo');",new{fixture.Est});
            await fixture.Migration("20261008_04_comunicacao.sql");await fixture.Migration("20261008_04_comunicacao.sql");
            await fixture.Migration("20261008_06_comunicacao_correcoes.sql");await fixture.Migration("20261008_06_comunicacao_correcoes.sql");
            await fixture.Migration("20261008_07_verify_comunicacao_correcoes.sql");
            await fixture.Migration("20261009_02_chat_emoji_reactions.sql");await fixture.Migration("20261009_02_chat_emoji_reactions.sql");
            await fixture.Migration("20261009_05_chat_forwarded.sql");await fixture.Migration("20261009_05_chat_forwarded.sql");return fixture;
        }
        public Task Migration(string name)=>Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Migrations","Delivery",name)));
        public async Task Execute(string sql,object? args=null){await using var db=await Source.OpenConnectionAsync();await db.ExecuteAsync(sql,args);}
        public async Task<T> Scalar<T>(string sql){await using var db=await Source.OpenConnectionAsync();return await db.ExecuteScalarAsync<T>(sql);}
        public async ValueTask DisposeAsync(){await Source.DisposeAsync();await using var db=new NpgsqlConnection(connection);await db.OpenAsync();await db.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");}
    }
}
