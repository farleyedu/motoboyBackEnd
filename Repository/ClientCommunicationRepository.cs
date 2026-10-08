using APIBack.DTOs.Atendimento;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository;

public sealed class ClientCommunicationRepository(NpgsqlDataSource source)
{
    public sealed class MessageRow : MotoboyClientChatMessageDto
    {
        public string? ProviderId { get; set; }
        public string? IncomingMedia { get; set; }
        public string? PhoneNumberId { get; set; }
    }
    private const string HistorySelect=@"SELECT m.id AS Id,m.conteudo AS Body,m.tipo::text AS Type,m.status::text AS Status,
 m.data_criacao AS CreatedAtUtc,m.criada_por=@Sender AS Mine,m.id_provedor AS ProviderId,
 (e.payload->'message'->(e.payload->'message'->>'type'))::text AS IncomingMedia,e.phone_number_id AS PhoneNumberId
 FROM mensagens m JOIN conversas c ON c.id=m.id_conversa
 JOIN conversas selected ON selected.id=@Conversation AND selected.id_estabelecimento=@Est
 LEFT JOIN wa_evento e ON e.tipo='mensagem' AND e.chave=m.id_provedor
 WHERE c.id_estabelecimento=@Est AND c.id_cliente=selected.id_cliente
 AND COALESCE(e.payload->'message'->>'type','')<>'reaction'";
    public async Task<(List<MessageRow> Messages,bool More)> HistoryAsync(Guid est,Guid conversation,string sender,DateTime? before,Guid beforeId,string? search,int limit)
    {
        await using var db=await source.OpenConnectionAsync();
        var rows=(await db.QueryAsync<MessageRow>($@"{HistorySelect}
 AND (CAST(@Before AS timestamptz) IS NULL OR (m.data_criacao,m.id)<(CAST(@Before AS timestamptz),CAST(@BeforeId AS uuid)))
 AND (CAST(@Search AS text) IS NULL OR strpos(lower(CONCAT_WS(' ',m.conteudo,e.payload->'message'->'image'->>'caption')),lower(CAST(@Search AS text)))>0)
 ORDER BY m.data_criacao DESC,m.id DESC LIMIT @Limit;",new{Est=est,Conversation=conversation,Sender=sender,Before=before,BeforeId=beforeId,Search=search,Limit=limit+1})).ToList();
        var more=rows.Count>limit;return (rows.Take(limit).Reverse().ToList(),more);
    }
    public async Task<MessageRow?> MessageAsync(Guid est,Guid conversation,string sender,Guid id)
    {
        await using var db=await source.OpenConnectionAsync();
        return await db.QuerySingleOrDefaultAsync<MessageRow>($"{HistorySelect} AND m.id=@Id;",new{Est=est,Conversation=conversation,Sender=sender,Id=id});
    }
    public async Task<IReadOnlyDictionary<Guid,CommunicationReactionDto[]>> ReactionsAsync(ChatActor actor,Guid[] ids)
    {
        await using var db=await source.OpenConnectionAsync();
        var rows=await db.QueryAsync<(Guid Id,string Reaction,int Count,bool Mine)>(@"SELECT message_id,reaction,COUNT(*)::integer,bool_or(actor_key=@Actor)
 FROM delivery_client_chat_reaction WHERE estabelecimento_id=@Est AND message_id=ANY(@Ids) GROUP BY message_id,reaction;",new{Est=actor.EstablishmentId,Actor=actor.Key,Ids=ids});
        var result=rows.GroupBy(r=>r.Id).ToDictionary(g=>g.Key,g=>g.Select(r=>new CommunicationReactionDto(r.Reaction,r.Count,r.Mine)).ToArray());
        var incoming=await db.QueryAsync<(Guid Id,string Emoji)>(@"SELECT m.id,latest.emoji FROM mensagens m JOIN conversas c ON c.id=m.id_conversa
 JOIN LATERAL (SELECT e.payload->'message'->'reaction'->>'emoji' AS emoji FROM wa_evento e
 WHERE e.tipo='mensagem' AND e.payload->'message'->>'type'='reaction'
 AND e.payload->'message'->'reaction'->>'message_id'=m.id_provedor
 ORDER BY CASE WHEN e.payload->'message'->>'timestamp' ~ '^[0-9]{1,15}$' THEN (e.payload->'message'->>'timestamp')::numeric ELSE 0 END DESC,e.recebido_em DESC LIMIT 1) latest ON TRUE
 WHERE c.id_estabelecimento=@Est AND m.id=ANY(@Ids);",new{Est=actor.EstablishmentId,Ids=ids});
        foreach(var row in incoming)
        {
            var key=row.Emoji switch{"\U0001F44D"=>"like","\u2764" or "\u2764\uFE0F"=>"heart","\U0001F64F"=>"thanks","\u26A0" or "\u26A0\uFE0F"=>"alert",_=>null};
            if(key==null)continue;
            var current=result.GetValueOrDefault(row.Id)??Array.Empty<CommunicationReactionDto>();
            var same=current.FirstOrDefault(r=>r.Reaction==key);
            result[row.Id]=current.Where(r=>r.Reaction!=key).Append(new(key,(same?.Count??0)+1,same?.Mine??false)).ToArray();
        }
        return result;
    }
    public async Task SetReactionAsync(ChatActor actor,Guid id,string? reaction)
    {
        await using var db=await source.OpenConnectionAsync();
        if(reaction==null)await db.ExecuteAsync("DELETE FROM delivery_client_chat_reaction WHERE estabelecimento_id=@Est AND message_id=@Id AND actor_key=@Actor;",new{Est=actor.EstablishmentId,Id=id,Actor=actor.Key});
        else await db.ExecuteAsync(@"INSERT INTO delivery_client_chat_reaction(estabelecimento_id,message_id,actor_key,reaction) VALUES(@Est,@Id,@Actor,@Reaction)
 ON CONFLICT(estabelecimento_id,message_id,actor_key) DO UPDATE SET reaction=EXCLUDED.reaction;",new{Est=actor.EstablishmentId,Id=id,Actor=actor.Key,Reaction=reaction});
    }
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
