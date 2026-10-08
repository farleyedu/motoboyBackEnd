using System.Text.Json;
using APIBack.DTOs.Atendimento;
using APIBack.Hubs;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository;

public sealed class CommunicationRepository(NpgsqlDataSource dataSource)
{
    private sealed class Row : CommunicationMessageDto
    {
        public Guid? AttachmentId { get; set; }
        public string? AttachmentName { get; set; }
        public string? AttachmentType { get; set; }
        public long AttachmentSize { get; set; }
        public string? Fingerprint { get; set; }
    }
    public sealed class FileRow : CommunicationAttachmentDto
    {
        public byte[] Content { get; set; } = Array.Empty<byte>();
        public string ThreadKey { get; set; } = "";
        public string OwnerKey { get; set; } = "";
    }
    private const string Select = @"
SELECT c.id AS Id,c.sequence AS Sequence,c.thread_key AS ThreadKey,c.channel AS Channel,
 c.client_id AS ClientId,c.body AS Body,c.sender_name AS SenderName,c.sender_key AS SenderKey,
 c.motoboy_id AS MotoboyId,c.recipient_id AS RecipientId,c.pedido_id AS PedidoId,
 c.created_at_utc AS CreatedAtUtc,c.mentions AS Mentions,c.reply_to AS ReplyTo,
 q.body AS ReplyBody,q.sender_name AS ReplySender,c.fingerprint AS Fingerprint,
 a.id AS AttachmentId,a.name AS AttachmentName,a.content_type AS AttachmentType,
 COALESCE(octet_length(a.content),0) AS AttachmentSize,
 CASE WHEN @IsMobile THEN c.sender_key=@Actor ELSE c.sender_key LIKE 'u:%' END AS Mine,
 EXISTS(SELECT 1 FROM delivery_chat_read r WHERE r.message_id=c.id AND r.actor_key=@Actor) AS Read,
 (SELECT COUNT(*)::integer FROM delivery_chat_read r WHERE r.message_id=c.id AND r.actor_key<>c.sender_key) AS ReadCount
FROM delivery_chat_message c LEFT JOIN delivery_chat_attachment a ON a.id=c.attachment_id
LEFT JOIN delivery_chat_message q ON q.id=c.reply_to";

    public async Task<CommunicationPageDto> ListAsync(ChatActor actor, ChatThread thread, long? before, string? search, int limit, int? pedidoId)
    {
        await using var db = await dataSource.OpenConnectionAsync();
        var rows = (await db.QueryAsync<Row>($@"{Select}
 WHERE c.estabelecimento_id=@Est AND c.thread_key=@Thread
 AND (@Before IS NULL OR c.sequence<@Before) AND (@PedidoId IS NULL OR c.pedido_id=@PedidoId)
 AND (@Search IS NULL OR strpos(lower(c.body),lower(@Search))>0)
 ORDER BY c.sequence DESC LIMIT @Limit;", new { Est = actor.EstablishmentId, Actor = actor.Key,
            IsMobile = actor.MotoboyId.HasValue, Thread = thread.Key, Before = before, Search = search, PedidoId = pedidoId, Limit = limit + 1 })).ToList();
        var more = rows.Count > limit;
        rows = rows.Take(limit).Reverse().ToList();
        await EnrichAsync(db, rows, actor);
        return new(rows, more, rows.FirstOrDefault()?.Sequence);
    }
    public async Task<CommunicationPageDto> ContextAsync(ChatActor actor, ChatThread thread, Guid id, int? pedidoId)
    {
        await using var db=await dataSource.OpenConnectionAsync();
        var args=new{Est=actor.EstablishmentId,Thread=thread.Key,Id=id,PedidoId=pedidoId,Actor=actor.Key,IsMobile=actor.MotoboyId.HasValue};
        var anchor=await db.ExecuteScalarAsync<long?>(@"SELECT sequence FROM delivery_chat_message WHERE id=@Id AND estabelecimento_id=@Est AND thread_key=@Thread
 AND (@PedidoId IS NULL OR pedido_id=@PedidoId);",args);
        if(!anchor.HasValue)throw new DeliveryDomainException(404,"CHAT_MESSAGE_NOT_FOUND","Mensagem indisponivel nesta conversa.");
        var rows=(await db.QueryAsync<Row>($@"{Select} WHERE c.estabelecimento_id=@Est AND c.thread_key=@Thread
 AND (@PedidoId IS NULL OR c.pedido_id=@PedidoId) AND c.id IN (
 SELECT id FROM (SELECT id FROM delivery_chat_message WHERE estabelecimento_id=@Est AND thread_key=@Thread
 AND (@PedidoId IS NULL OR pedido_id=@PedidoId) AND sequence<=@Anchor ORDER BY sequence DESC LIMIT 25) older
 UNION SELECT id FROM (SELECT id FROM delivery_chat_message WHERE estabelecimento_id=@Est AND thread_key=@Thread
 AND (@PedidoId IS NULL OR pedido_id=@PedidoId) AND sequence>@Anchor ORDER BY sequence LIMIT 25) newer)
 ORDER BY c.sequence;",new{args.Est,args.Thread,args.PedidoId,args.Actor,args.IsMobile,Anchor=anchor.Value})).ToList();
        await EnrichAsync(db,rows,actor);
        return new(rows,false,rows.FirstOrDefault()?.Sequence);
    }
    private sealed class ReactionRow
    {
        public Guid MessageId { get; set; }
        public string Reaction { get; set; } = "";
        public int Count { get; set; }
        public bool Mine { get; set; }
    }
    private static async Task EnrichAsync(NpgsqlConnection db, List<Row> rows, ChatActor actor, NpgsqlTransaction? tx = null)
    {
        foreach (var row in rows)
        {
            row.Attachment = row.AttachmentId.HasValue ? new CommunicationAttachmentDto { Id = row.AttachmentId.Value, Name = row.AttachmentName!, ContentType = row.AttachmentType!, Size = row.AttachmentSize } : null;
            row.Mentioned = actor.MotoboyId.HasValue && row.Mentions.Contains(actor.MotoboyId.Value);
        }
        if (rows.Count == 0) return;
        var reactions = await db.QueryAsync<ReactionRow>(@"
SELECT message_id AS MessageId,reaction AS Reaction,COUNT(*)::integer AS Count,bool_or(actor_key=@Actor) AS Mine
FROM delivery_chat_reaction WHERE message_id=ANY(@Ids) GROUP BY message_id,reaction;",
            new { Actor = actor.Key, Ids = rows.Select(r => r.Id).ToArray() }, tx);
        foreach (var row in rows) row.Reactions = reactions.Where(r => r.MessageId == row.Id).Select(r => new CommunicationReactionDto(r.Reaction, r.Count, r.Mine)).ToArray();
    }
    public async Task<CommunicationMessageDto> SendAsync(ChatActor actor, ChatThread thread, SendCommunicationRequest request, string name, string fingerprint)
    {
        await using var db = await dataSource.OpenConnectionAsync();
        await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@Key,0));", new { Key = $"{actor.EstablishmentId}:{actor.Key}:{request.ClientId}" }, tx);
        var existing = await db.QuerySingleOrDefaultAsync<Row>($"{Select} WHERE c.estabelecimento_id=@Est AND c.sender_key=@Actor AND c.client_id=@ClientId;",
            new { Est = actor.EstablishmentId, Actor = actor.Key, IsMobile = actor.MotoboyId.HasValue, request.ClientId }, tx);
        if (existing != null)
        {
            if (existing.Fingerprint != fingerprint) throw new DeliveryDomainException(409, "CHAT_ID_CONFLICT", "Este envio ja foi utilizado com outro conteudo.");
            await EnrichAsync(db, new() { existing }, actor, tx);
            await tx.CommitAsync();
            return existing;
        }
        var participants = new[] { actor.MotoboyId, thread.TargetId }.Where(x => x.HasValue).Select(x => x!.Value).Concat(request.Mentions).Distinct().ToArray();
        var active = await db.QueryAsync<int>(@"SELECT motoboy_id FROM motoboy_estabelecimento
 WHERE estabelecimento_id=@Est AND ativo=TRUE AND motoboy_id=ANY(@Ids) FOR SHARE;", new { Est = actor.EstablishmentId, Ids = participants }, tx);
        if (active.Distinct().Count() != participants.Length) throw new DeliveryDomainException(403, "CHAT_LINK_FORBIDDEN", "Um participante nao esta vinculado a esta loja.");
        if (request.PedidoId.HasValue && !await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM pedido WHERE id=@Id AND id_estabelecimento=@Est);", new { Id = request.PedidoId, Est = actor.EstablishmentId }, tx))
            throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado nesta loja.");
        if (request.ReplyTo.HasValue && !await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(SELECT 1 FROM delivery_chat_message WHERE id=@Id AND estabelecimento_id=@Est AND thread_key=@Thread);",
            new { Id = request.ReplyTo, Est = actor.EstablishmentId, Thread = thread.Key }, tx))
            throw new DeliveryDomainException(422, "CHAT_REPLY_INVALID", "A mensagem citada nao pertence a esta conversa.");
        if (request.AttachmentId.HasValue && !await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(SELECT 1 FROM delivery_chat_attachment WHERE id=@Id AND estabelecimento_id=@Est AND thread_key=@Thread AND owner_key=@Actor);",
            new { Id = request.AttachmentId, Est = actor.EstablishmentId, Thread = thread.Key, Actor = actor.Key }, tx))
            throw new DeliveryDomainException(422, "CHAT_ATTACHMENT_INVALID", "Anexo indisponivel nesta conversa.");
        Guid id;
        var body = request.Body ?? "";
        var legacyBody = string.IsNullOrWhiteSpace(body) ? "[Anexo]" : body;
        if (thread.Channel is "store" or "group")
        {
            var sql = thread.Channel == "store" ? @"INSERT INTO delivery_motoboy_message(estabelecimento_id,motoboy_id,pedido_id,direction,body,sent_by_user_id)
 VALUES(@Est,@Target,@PedidoId,@Direction,@Body,@UserId) RETURNING id;" : @"INSERT INTO motoboy_group_message(estabelecimento_id,sender_type,motoboy_id,sent_by_user_id,body)
 VALUES(@Est,@Direction,@MotoboyId,@UserId,@Body) RETURNING id;";
            var legacyId = await db.ExecuteScalarAsync<long>(sql, new { Est = actor.EstablishmentId, Target = thread.TargetId,
                request.PedidoId, Direction = actor.MotoboyId.HasValue ? "motoboy" : "operator", Body = legacyBody,
                actor.MotoboyId, UserId = actor.MotoboyId.HasValue ? (int?)null : actor.UserId }, tx);
            id = await db.ExecuteScalarAsync<Guid>("SELECT id FROM delivery_chat_message WHERE channel=@Channel AND legacy_id=@LegacyId;", new { thread.Channel, LegacyId = legacyId }, tx);
        }
        else
        {
            id = Guid.NewGuid();
            await db.ExecuteAsync(@"INSERT INTO delivery_chat_message(id,estabelecimento_id,thread_key,channel,sender_key,sender_name,motoboy_id,recipient_id,body)
 VALUES(@Id,@Est,@Thread,'private',@Actor,@Name,@MotoboyId,@Target,@Body);", new { Id = id, Est = actor.EstablishmentId, Thread = thread.Key, Actor = actor.Key, Name = name, actor.MotoboyId, Target = thread.TargetId, Body = body }, tx);
        }
        await db.ExecuteAsync(@"UPDATE delivery_chat_message SET body=@Body,client_id=@ClientId,fingerprint=@Fingerprint,
 sender_name=@Name,attachment_id=@AttachmentId,reply_to=@ReplyTo,mentions=@Mentions WHERE id=@Id;",
            new { Id = id, Body = body, request.ClientId, Fingerprint = fingerprint, Name = name, request.AttachmentId, request.ReplyTo, request.Mentions }, tx);
        var row = await db.QuerySingleAsync<Row>($"{Select} WHERE c.id=@Id;", new { Id = id, Actor = actor.Key, IsMobile = actor.MotoboyId.HasValue }, tx);
        await EnrichAsync(db, new() { row }, actor, tx);
        await EmitAsync(db, tx, actor, thread, row.Sequence, "message", id);
        await tx.CommitAsync();
        return row;
    }
    public async Task<FileRow?> FileAsync(Guid est, Guid id)
    {
        await using var db = await dataSource.OpenConnectionAsync();
        return await db.QuerySingleOrDefaultAsync<FileRow>(@"SELECT id AS Id,name AS Name,content_type AS ContentType,
 octet_length(content) AS Size,content AS Content,thread_key AS ThreadKey,owner_key AS OwnerKey
 FROM delivery_chat_attachment WHERE id=@Id AND estabelecimento_id=@Est;", new { Id = id, Est = est });
    }
    public async Task<CommunicationAttachmentDto> SaveFileAsync(ChatActor actor, ChatThread thread, string name, string type, byte[] content)
    {
        var result = new CommunicationAttachmentDto { Id = Guid.NewGuid(), Name = name, ContentType = type, Size = content.LongLength };
        await using var db = await dataSource.OpenConnectionAsync();
        await db.ExecuteAsync(@"INSERT INTO delivery_chat_attachment(id,estabelecimento_id,thread_key,owner_key,name,content_type,content)
 VALUES(@Id,@Est,@Thread,@Actor,@Name,@Type,@Content);", new { result.Id, Est = actor.EstablishmentId, Thread = thread.Key, Actor = actor.Key, Name = name, Type = type, Content = content });
        return result;
    }
    public async Task<bool> FilePublishedAsync(Guid est, Guid id)
    {
        await using var db = await dataSource.OpenConnectionAsync();
        return await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(SELECT 1 FROM delivery_chat_message WHERE estabelecimento_id=@Est AND attachment_id=@Id)
 OR EXISTS(SELECT 1 FROM delivery_client_chat_dispatch WHERE estabelecimento_id=@Est AND attachment_id=@Id AND state='sent');", new { Est = est, Id = id });
    }
    public async Task ReadAsync(ChatActor actor, ChatThread thread, long through)
    {
        await using var db = await dataSource.OpenConnectionAsync();
        await using var tx = await db.BeginTransactionAsync();
        var args = new { Est = actor.EstablishmentId, Thread = thread.Key, Actor = actor.Key, IsMobile = actor.MotoboyId.HasValue, Through = through };
        var changed = await db.ExecuteAsync(@"INSERT INTO delivery_chat_read(message_id,actor_key)
 SELECT id,@Actor FROM delivery_chat_message WHERE estabelecimento_id=@Est AND thread_key=@Thread AND sequence<=@Through
 AND CASE WHEN @IsMobile THEN sender_key<>@Actor ELSE sender_key NOT LIKE 'u:%' END ON CONFLICT DO NOTHING;", args, tx);
        if (thread.Channel == "store") await db.ExecuteAsync(@"UPDATE delivery_motoboy_message l SET read_at_utc=COALESCE(l.read_at_utc,NOW())
 FROM delivery_chat_message c WHERE c.channel='store' AND c.legacy_id=l.id AND c.estabelecimento_id=@Est AND c.thread_key=@Thread AND c.sequence<=@Through AND l.direction=@Direction;",
            new { Est = actor.EstablishmentId, Thread = thread.Key, Through = through, Direction = actor.MotoboyId.HasValue ? "operator" : "motoboy" }, tx);
        if (changed > 0) await EmitAsync(db, tx, actor, thread, through, "read", null);
        await tx.CommitAsync();
    }
    public async Task ReactAsync(ChatActor actor, ChatThread thread, Guid id, string? reaction)
    {
        await using var db = await dataSource.OpenConnectionAsync();
        await using var tx = await db.BeginTransactionAsync();
        var seq = await db.QuerySingleOrDefaultAsync<long?>("SELECT sequence FROM delivery_chat_message WHERE estabelecimento_id=@Est AND thread_key=@Thread AND id=@Id;", new { Est = actor.EstablishmentId, Thread = thread.Key, Id = id }, tx);
        if (seq == null) throw new DeliveryDomainException(404, "CHAT_MESSAGE_NOT_FOUND", "Mensagem nao encontrada.");
        if (reaction == null) await db.ExecuteAsync("DELETE FROM delivery_chat_reaction WHERE message_id=@Id AND actor_key=@Actor;", new { Id = id, Actor = actor.Key }, tx);
        else await db.ExecuteAsync(@"INSERT INTO delivery_chat_reaction(message_id,actor_key,reaction) VALUES(@Id,@Actor,@Reaction)
 ON CONFLICT(message_id,actor_key) DO UPDATE SET reaction=EXCLUDED.reaction;", new { Id = id, Actor = actor.Key, Reaction = reaction }, tx);
        await EmitAsync(db, tx, actor, thread, seq.Value, "reaction", id);
        await tx.CommitAsync();
    }
    public async Task<IReadOnlyList<CommunicationMessageDto>> NotificationsAsync(ChatActor actor)
    {
        await using var db = await dataSource.OpenConnectionAsync();
        var rows = (await db.QueryAsync<Row>($@"{Select}
 WHERE c.estabelecimento_id=@Est AND c.sender_key<>@Actor
 AND (c.thread_key='group' OR c.thread_key=@Store OR c.channel='private' AND (c.motoboy_id=@MotoboyId OR c.recipient_id=@MotoboyId))
 AND NOT EXISTS(SELECT 1 FROM delivery_chat_notification_read n WHERE n.message_id=c.id AND n.actor_key=@Actor)
 AND NOT EXISTS(SELECT 1 FROM delivery_chat_read r WHERE r.message_id=c.id AND r.actor_key=@Actor)
 ORDER BY c.sequence DESC LIMIT 100;", new { Est = actor.EstablishmentId, Actor = actor.Key, IsMobile = true, Store = $"store:{actor.MotoboyId}", actor.MotoboyId })).ToList();
        await EnrichAsync(db, rows, actor);
        return rows;
    }
    public async Task DismissAsync(ChatActor actor, Guid id)
    {
        await using var db = await dataSource.OpenConnectionAsync();
        await db.ExecuteAsync(@"INSERT INTO delivery_chat_notification_read(message_id,actor_key)
 SELECT id,@Actor FROM delivery_chat_message WHERE id=@Id AND estabelecimento_id=@Est
 AND (thread_key='group' OR thread_key=@Store OR channel='private' AND (motoboy_id=@MotoboyId OR recipient_id=@MotoboyId)) ON CONFLICT DO NOTHING;",
            new { Id = id, Est = actor.EstablishmentId, Actor = actor.Key, Store = $"store:{actor.MotoboyId}", actor.MotoboyId });
    }
    private static async Task EmitAsync(NpgsqlConnection db, NpgsqlTransaction tx, ChatActor actor, ChatThread thread, long version, string action, Guid? id)
    {
        // Conversas privadas nunca sao publicadas no grupo administrativo da loja.
        var targets = new List<string>();
        if (thread.Channel != "private") targets.Add(DeliveryRealtimeEvents.EstablishmentGroup(actor.EstablishmentId));
        var sessions = await db.QueryAsync<Guid>(@"SELECT session_id FROM motoboy_active_sessions WHERE id_estabelecimento=@Est
 AND ended_at_utc IS NULL AND expires_at_utc>NOW() AND (@Channel='group' OR motoboy_id=ANY(@Ids));",
            new { Est = actor.EstablishmentId, thread.Channel, Ids = new[] { actor.MotoboyId, thread.TargetId }.Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray() }, tx);
        targets.AddRange(sessions.Select(DeliveryRealtimeEvents.SessionGroup));
        var payload = JsonSerializer.Serialize(new { schemaVersion = 1, estabelecimentoId = actor.EstablishmentId, channel = thread.Channel, threadKey = thread.Key, action, messageId = id, version });
        if(action=="message"&&id.HasValue) await db.ExecuteAsync(@"INSERT INTO delivery_chat_push_outbox(message_id,token,mentioned,recipient_estabelecimento_id,recipient_motoboy_id,recipient_session_id)
 SELECT c.id,p.token,p.motoboy_id=ANY(c.mentions),p.estabelecimento_id,p.motoboy_id,p.session_id FROM delivery_chat_message c JOIN delivery_chat_push_subscription p ON p.estabelecimento_id=c.estabelecimento_id
 JOIN motoboy_active_sessions s ON s.session_id=p.session_id AND s.ended_at_utc IS NULL AND s.expires_at_utc>NOW()
 JOIN motoboy_estabelecimento me ON me.motoboy_id=p.motoboy_id AND me.estabelecimento_id=p.estabelecimento_id AND me.ativo=TRUE
 WHERE c.id=@Id AND c.sender_key<>'m:'||p.motoboy_id
 AND (c.channel='group' OR c.channel='store' AND c.thread_key='store:'||p.motoboy_id OR c.channel='private' AND (c.motoboy_id=p.motoboy_id OR c.recipient_id=p.motoboy_id))
 ON CONFLICT DO NOTHING;",new{Id=id},tx);
        foreach (var target in targets.Distinct()) await db.ExecuteAsync(@"INSERT INTO delivery_realtime_outbox(event_id,event_name,target_group,estabelecimento_id,motoboy_id,aggregate_version,payload,occurred_at_utc)
 VALUES(@EventId,'delivery.chat.updated',@Target,@Est,@MotoboyId,@Version,@Payload::jsonb,NOW());",
            new { EventId = Guid.NewGuid(), Target = target, Est = actor.EstablishmentId, actor.MotoboyId, Version = version, Payload = payload }, tx);
    }
}
