using APIBack.DTOs.Atendimento;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository;

public sealed class ClientCommunicationRepository(NpgsqlDataSource source)
{
    public sealed class Dispatch
    {
        public string State {get;set;}="";
        public string Fingerprint {get;set;}="";
        public Guid? MessageId {get;set;}
        public Guid? AttachmentId {get;set;}
        public Guid? ReplyTo {get;set;}
        public string Body {get;set;}="";
        public DateTimeOffset CreatedAtUtc {get;set;}
    }
    public async Task<Dispatch?> ClaimAsync(ChatActor actor,int pedido,SendCommunicationRequest request,string fingerprint)
    {
        await using var db=await source.OpenConnectionAsync();
        await using var tx=await db.BeginTransactionAsync();
        await db.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@Key,0));",new{Key=$"client:{actor.EstablishmentId}:{actor.Key}:{request.ClientId}"},tx);
        var args=new{Est=actor.EstablishmentId,Actor=actor.Key,request.ClientId,Pedido=pedido,Fingerprint=fingerprint,request.AttachmentId,request.ReplyTo,request.Body};
        var previous=await db.QuerySingleOrDefaultAsync<Dispatch>(@"SELECT state AS State,fingerprint AS Fingerprint,message_id AS MessageId,attachment_id AS AttachmentId,reply_to AS ReplyTo,body AS Body,created_at_utc AS CreatedAtUtc
 FROM delivery_client_chat_dispatch WHERE estabelecimento_id=@Est AND actor_key=@Actor AND client_id=@ClientId;",args,tx);
        if(previous!=null)
        {
            if(previous.Fingerprint!=fingerprint)throw new DeliveryDomainException(409,"CHAT_ID_CONFLICT","Identificador usado com outro conteudo.");
            if(previous.State!="sent")throw new DeliveryDomainException(409,"CHAT_SEND_UNCERTAIN","O envio ao cliente esta em conferência. Consulte a loja antes de enviar novamente.");
            await tx.CommitAsync();return previous;
        }
        await db.ExecuteAsync(@"INSERT INTO delivery_client_chat_dispatch(estabelecimento_id,actor_key,client_id,pedido_id,fingerprint,state,attachment_id,reply_to,body)
 VALUES(@Est,@Actor,@ClientId,@Pedido,@Fingerprint,'sending',@AttachmentId,@ReplyTo,@Body);",args,tx);
        await tx.CommitAsync();return null;
    }
    public async Task FinishAsync(ChatActor actor,Guid clientId,Guid? messageId)
    {
        await using var db=await source.OpenConnectionAsync();
        await db.ExecuteAsync(@"UPDATE delivery_client_chat_dispatch SET state=@State,message_id=@MessageId
 WHERE estabelecimento_id=@Est AND actor_key=@Actor AND client_id=@ClientId;",new{Est=actor.EstablishmentId,Actor=actor.Key,ClientId=clientId,MessageId=messageId,State=messageId.HasValue?"sent":"uncertain"});
    }
    public async Task<IReadOnlyDictionary<Guid,Dispatch>> MetadataAsync(Guid est,Guid[] ids)
    {
        await using var db=await source.OpenConnectionAsync();
        var rows=await db.QueryAsync<Dispatch>(@"SELECT message_id AS MessageId,attachment_id AS AttachmentId,reply_to AS ReplyTo,body AS Body,created_at_utc AS CreatedAtUtc
 FROM delivery_client_chat_dispatch WHERE estabelecimento_id=@Est AND message_id=ANY(@Ids);",new{Est=est,Ids=ids});
        return rows.ToDictionary(r=>r.MessageId!.Value);
    }
}
