using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using APIBack.DTOs.Atendimento;
using Dapper;
using Npgsql;

namespace APIBack.Service;

public sealed class ChatPushService(NpgsqlDataSource source)
{
    public async Task RegisterAsync(ChatActor actor,Guid sessionId,ChatPushRequest request)
    {
        if(!actor.MotoboyId.HasValue||!Regex.IsMatch(request.Token,@"^(ExponentPushToken|ExpoPushToken)\[[A-Za-z0-9_-]{10,200}\]$"))throw new DeliveryDomainException(422,"PUSH_TOKEN_INVALID","Token de notificacao invalido.");
        await using var db=await source.OpenConnectionAsync();
        await using var tx=await db.BeginTransactionAsync();
        await db.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@Token,0));",new{request.Token},tx);
        await db.ExecuteAsync(@"UPDATE delivery_chat_push_outbox SET state='dismissed' WHERE token=@Token AND state IN ('queued','sending')
 AND (recipient_estabelecimento_id IS DISTINCT FROM @Est OR recipient_motoboy_id IS DISTINCT FROM @Motoboy OR recipient_session_id IS DISTINCT FROM @Session);",
            new{request.Token,Est=actor.EstablishmentId,Motoboy=actor.MotoboyId,Session=sessionId},tx);
        await db.ExecuteAsync(@"INSERT INTO delivery_chat_push_subscription(token,estabelecimento_id,motoboy_id,session_id,sound,vibration,muted,mention_alerts)
 VALUES(@Token,@Est,@Motoboy,@Session,@Sound,@Vibration,@Muted,@MentionAlerts)
 ON CONFLICT(token) DO UPDATE SET estabelecimento_id=EXCLUDED.estabelecimento_id,motoboy_id=EXCLUDED.motoboy_id,session_id=EXCLUDED.session_id,
 sound=EXCLUDED.sound,vibration=EXCLUDED.vibration,muted=EXCLUDED.muted,mention_alerts=EXCLUDED.mention_alerts,updated_at_utc=NOW();",
            new{request.Token,Est=actor.EstablishmentId,Motoboy=actor.MotoboyId,Session=sessionId,request.Sound,request.Vibration,request.Muted,request.MentionAlerts},tx);
        await tx.CommitAsync();
    }
}

public sealed class ChatPushWorker(NpgsqlDataSource source,IHttpClientFactory http,IConfiguration config,ILogger<ChatPushWorker> logger):BackgroundService
{
    internal sealed class Pending
    {
        public long Id{get;set;}public Guid MessageId{get;set;}public string Token{get;set;}="";
        public string Channel{get;set;}="";public string ThreadKey{get;set;}="";public bool Mentioned{get;set;}
        public bool Sound{get;set;}public bool Vibration{get;set;}public bool Audible{get;set;}
        public Guid SessionId{get;set;}
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try{if(config.GetValue<bool>("Communication:PushEnabled"))await PublishAsync(stoppingToken);}
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
            catch(PostgresException ex)when(ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn){/* Habilitacao somente depois da migration. */}
            catch(Exception ex){logger.LogWarning(ex,"Publicacao de avisos de chat indisponivel.");}
            try{await Task.Delay(TimeSpan.FromSeconds(8),stoppingToken);}catch(OperationCanceledException){break;}
        }
    }
    internal async Task<List<Pending>> ClaimAsync(CancellationToken ct)
    {
        await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
        var rows=(await db.QueryAsync<Pending>(@"SELECT p.id AS Id,p.message_id AS MessageId,p.token AS Token,c.channel AS Channel,c.thread_key AS ThreadKey,
 p.mentioned AS Mentioned,s.sound AS Sound,s.vibration AS Vibration,s.session_id AS SessionId,
 CASE WHEN p.mentioned THEN s.mention_alerts ELSE NOT(c.channel='group' AND s.muted) END AS Audible
 FROM delivery_chat_push_outbox p JOIN delivery_chat_message c ON c.id=p.message_id JOIN delivery_chat_push_subscription s ON s.token=p.token
 JOIN motoboy_active_sessions a ON a.session_id=s.session_id AND a.motoboy_id=s.motoboy_id AND a.id_estabelecimento=s.estabelecimento_id AND a.ended_at_utc IS NULL AND a.expires_at_utc>NOW()
 JOIN motoboy_estabelecimento me ON me.motoboy_id=s.motoboy_id AND me.estabelecimento_id=s.estabelecimento_id AND me.ativo=TRUE
 WHERE p.state='queued' AND p.created_at_utc>NOW()-interval '5 minutes' AND c.estabelecimento_id=s.estabelecimento_id
 AND p.recipient_estabelecimento_id=s.estabelecimento_id AND p.recipient_motoboy_id=s.motoboy_id AND p.recipient_session_id=s.session_id
 AND c.sender_key<>'m:'||s.motoboy_id
 AND (c.channel='group' OR c.channel='store' AND c.thread_key='store:'||s.motoboy_id
 OR c.channel='private' AND (c.motoboy_id=s.motoboy_id OR c.recipient_id=s.motoboy_id))
 AND NOT EXISTS(SELECT 1 FROM delivery_chat_read r WHERE r.message_id=c.id AND r.actor_key='m:'||s.motoboy_id)
 AND (p.mentioned OR NOT(c.channel='group' AND s.muted))
 ORDER BY p.id LIMIT 20 FOR UPDATE OF p SKIP LOCKED;",transaction:tx)).ToList();
        if(rows.Count==0){await tx.CommitAsync(ct);return rows;}
        await db.ExecuteAsync("UPDATE delivery_chat_push_outbox SET state='sending' WHERE id=ANY(@Ids)",new{Ids=rows.Select(r=>r.Id).ToArray()},tx);await tx.CommitAsync(ct);
        return rows;
    }
    internal async Task<bool> CanPublishAsync(long id)
    {
        await using var db=await source.OpenConnectionAsync();
        return await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(SELECT 1 FROM delivery_chat_push_outbox p
 JOIN delivery_chat_push_subscription s ON s.token=p.token AND s.estabelecimento_id=p.recipient_estabelecimento_id
 AND s.motoboy_id=p.recipient_motoboy_id AND s.session_id=p.recipient_session_id
 JOIN motoboy_active_sessions a ON a.session_id=s.session_id AND a.motoboy_id=s.motoboy_id AND a.id_estabelecimento=s.estabelecimento_id
 JOIN motoboy_estabelecimento me ON me.motoboy_id=s.motoboy_id AND me.estabelecimento_id=s.estabelecimento_id
 WHERE p.id=@Id AND p.state='sending' AND a.ended_at_utc IS NULL AND a.expires_at_utc>NOW() AND me.ativo=TRUE);",new{Id=id});
    }
    private async Task PublishAsync(CancellationToken ct)
    {
        var rows=await ClaimAsync(ct);
        // Nao repetir automaticamente um POST sem ACK do provedor: pode duplicar o alerta.
        foreach(var row in rows)
        {
            try
            {
                if(!await CanPublishAsync(row.Id))continue;
                var audible=row.Sound&&row.Audible;
                var payload=new{to=row.Token,title=row.Mentioned?"Voce foi mencionado":"Nova mensagem da equipe",body=row.Mentioned?"Uma mensagem precisa da sua atencao. Toque para abrir.":"Toque para abrir a conversa.",
                    sound=audible?(row.Mentioned?"chat_mention.wav":"chat_message.wav"):null,
                    channelId=$"chat-{(row.Mentioned?"mentions":"messages")}-{(audible?"sound":"silent")}-{(row.Vibration&&row.Audible?"vibrate":"quiet")}-v2",
                    priority=row.Mentioned?"high":"normal",data=new{chatMessageId=row.MessageId,channel=row.Channel,threadKey=row.ThreadKey,recipientSessionId=row.SessionId}};
                using var client=http.CreateClient();
                var access=config["Communication:ExpoAccessToken"];if(!string.IsNullOrWhiteSpace(access))client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",access);
                using var response=await client.PostAsJsonAsync("https://exp.host/--/api/v2/push/send",payload,ct);response.EnsureSuccessStatusCode();
                using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var ticket=json.RootElement.GetProperty("data");
                var success=ticket.GetProperty("status").GetString()=="ok";
                var ticketId=success?ticket.GetProperty("id").GetString():null;
                await using var db=await source.OpenConnectionAsync(ct);
                await db.ExecuteAsync("UPDATE delivery_chat_push_outbox SET state=@State,ticket_id=@Ticket WHERE id=@Id AND state='sending'",new{row.Id,State=success?"sent":"failed",Ticket=ticketId});
                if(!success&&ticket.TryGetProperty("details",out var details)&&details.TryGetProperty("error",out var error)&&error.GetString()=="DeviceNotRegistered")await db.ExecuteAsync("DELETE FROM delivery_chat_push_subscription WHERE token=@Token",new{row.Token});
            }
            catch(Exception ex)when(ex is not OperationCanceledException){await using var db=await source.OpenConnectionAsync(ct);await db.ExecuteAsync("UPDATE delivery_chat_push_outbox SET state='failed' WHERE id=@Id AND state='sending'",new{row.Id});logger.LogWarning("Aviso de chat {Id} sem confirmacao: {Type}",row.Id,ex.GetType().Name);}
        }
    }
}
