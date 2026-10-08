namespace APIBack.DTOs.Atendimento;

public sealed record ChatActor(Guid EstablishmentId, int UserId, int? MotoboyId)
{
    public string Key => MotoboyId.HasValue ? $"m:{MotoboyId}" : $"u:{UserId}";
}
public sealed record ChatThread(string Channel, string Key, int? TargetId);
public sealed class SendCommunicationRequest
{
    public Guid ClientId { get; set; }
    public string? Body { get; set; }
    public Guid? AttachmentId { get; set; }
    public Guid? ReplyTo { get; set; }
    public int[] Mentions { get; set; } = Array.Empty<int>();
    public int? PedidoId { get; set; }
}
public class CommunicationAttachmentDto
{
    public int? ClientPedidoId { get; set; }
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
}
public class CommunicationMessageDto
{
    public Guid Id { get; set; }
    public long Sequence { get; set; }
    public string Channel { get; set; } = "";
    public string ThreadKey { get; set; } = "";
    public Guid? ClientId { get; set; }
    public string Body { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string SenderKey { get; set; } = "";
    public int? MotoboyId { get; set; }
    public int? RecipientId { get; set; }
    public int? PedidoId { get; set; }
    public bool Mine { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public int ReadCount { get; set; }
    public bool Read { get; set; }
    public bool Mentioned { get; set; }
    public int[] Mentions { get; set; } = Array.Empty<int>();
    public CommunicationAttachmentDto? Attachment { get; set; }
    public Guid? ReplyTo { get; set; }
    public string? ReplyBody { get; set; }
    public string? ReplySender { get; set; }
    public IReadOnlyList<CommunicationReactionDto> Reactions { get; set; } = Array.Empty<CommunicationReactionDto>();
}
public sealed record CommunicationReactionDto(string Reaction, int Count, bool Mine);
public sealed record CommunicationPageDto(IReadOnlyList<CommunicationMessageDto> Messages, bool HasMore, long? Cursor);
public sealed class CommunicationReadRequest { public long Through { get; set; } }
public sealed class CommunicationReactionRequest { public string? Reaction { get; set; } }
