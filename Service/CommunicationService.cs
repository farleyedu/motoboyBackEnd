using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using APIBack.DTOs.Atendimento;
using APIBack.Repository;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;

namespace APIBack.Service;

public sealed class CommunicationService(CommunicationRepository repository, IAtendimentoRepository atendimento, IPedidoQueueService queues)
{
    internal static ChatThread Resolve(ChatActor actor, string channel, int? target)
    {
        if (actor.EstablishmentId == Guid.Empty || actor.UserId <= 0) throw new DeliveryDomainException(401, "UNAUTHENTICATED", "Contexto invalido.");
        return channel switch
        {
            "group" => new(channel, "group", null),
            "client" when actor.MotoboyId is > 0 && target is > 0 => new(channel,$"client:{target}",target),
            "store" when (actor.MotoboyId ?? target) is > 0 => new(channel, $"store:{actor.MotoboyId ?? target}", actor.MotoboyId ?? target),
            "private" when actor.MotoboyId is > 0 && target is > 0 && target != actor.MotoboyId => new(channel, $"private:{Math.Min(actor.MotoboyId.Value, target.Value)}:{Math.Max(actor.MotoboyId.Value, target.Value)}", target),
            _ => throw new DeliveryDomainException(422, "CHAT_CHANNEL_INVALID", "Canal ou participante invalido.")
        };
    }
    private async Task<ChatThread> AuthorizeAsync(ChatActor actor, string channel, int? target)
    {
        var thread = Resolve(actor, channel, target);
        if(channel=="client")
        {
            var queue=await queues.GetQueueAsync(actor.EstablishmentId,actor.MotoboyId!.Value);
            if(!AtendimentoService.PedidoEstaNaFila(queue,target!.Value)) throw new DeliveryDomainException(403,"PEDIDO_NOT_IN_YOUR_QUEUE","Este pedido nao esta na sua fila.");
            return thread;
        }
        var contacts = await atendimento.ListMotoboysVinculadosAsync(actor.EstablishmentId);
        if (actor.MotoboyId.HasValue && !contacts.Any(c => c.MotoboyId == actor.MotoboyId)
            || thread.TargetId.HasValue && !contacts.Any(c => c.MotoboyId == thread.TargetId))
            throw new DeliveryDomainException(403, "CHAT_LINK_FORBIDDEN", "Participante nao vinculado a esta loja.");
        return thread;
    }
    public async Task<CommunicationPageDto> ListAsync(ChatActor actor, string channel, int? target, long? before, string? search, int limit, int? pedidoId) =>
        await repository.ListAsync(actor, await InternalAsync(actor, channel, target), before, string.IsNullOrWhiteSpace(search) ? null : search.Trim()[..Math.Min(search.Trim().Length, 100)], Math.Clamp(limit, 1, 100), pedidoId);
    private Task<ChatThread> InternalAsync(ChatActor actor,string channel,int? target)
    {
        if(channel=="client")throw new DeliveryDomainException(422,"CHAT_CHANNEL_INVALID","Use o canal autorizado do pedido.");
        return AuthorizeAsync(actor,channel,target);
    }
    internal static void Validate(SendCommunicationRequest request)
    {
        request.Body = request.Body?.Trim() ?? "";
        request.Mentions = (request.Mentions ?? Array.Empty<int>()).Distinct().Order().ToArray();
        if (request.ClientId == Guid.Empty || request.Body.Length > 500 || request.Body.Length == 0 && !request.AttachmentId.HasValue || request.Mentions.Length > 30 || request.Mentions.Any(id => id <= 0))
            throw new DeliveryDomainException(422, "CHAT_MESSAGE_INVALID", "Envie texto de ate 500 caracteres ou um anexo valido.");
    }
    public async Task<CommunicationMessageDto> SendAsync(ChatActor actor, string channel, int? target, SendCommunicationRequest request)
    {
        Validate(request);
        var thread = await InternalAsync(actor, channel, target);
        if (channel != "group" && request.Mentions.Length > 0) throw new DeliveryDomainException(422, "CHAT_MENTION_INVALID", "Mencoes de participantes sao permitidas no grupo.");
        var name = actor.MotoboyId.HasValue ? await atendimento.ObterNomeMotoboyAsync(actor.MotoboyId.Value) ?? "Motoboy" : "Loja";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { thread.Key, request.Body, request.AttachmentId, request.ReplyTo, request.Mentions, request.PedidoId }))));
        return await repository.SendAsync(actor, thread, request, name, hash);
    }
    public async Task ReadAsync(ChatActor actor, string channel, int? target, long through)
    {
        if (through <= 0) throw new DeliveryDomainException(422, "CHAT_CURSOR_INVALID", "Ultima mensagem invalida.");
        await repository.ReadAsync(actor, await InternalAsync(actor, channel, target), through);
    }
    public async Task ReactAsync(ChatActor actor, string channel, int? target, Guid id, string? reaction)
    {
        if (reaction != null && reaction is not ("like" or "heart" or "thanks" or "alert")) throw new DeliveryDomainException(422, "CHAT_REACTION_INVALID", "Reacao invalida.");
        await repository.ReactAsync(actor, await InternalAsync(actor, channel, target), id, reaction);
    }
    public async Task<CommunicationAttachmentDto> UploadAsync(ChatActor actor, string channel, int? target, IFormFile file, CancellationToken ct)
    {
        var thread = await AuthorizeAsync(actor, channel, target);
        if(channel=="client")
        {
            var canal=await atendimento.GetCanalDoPedidoAsync(actor.EstablishmentId,target!.Value);
            if(!canal.PodeReceber)throw new DeliveryDomainException(422,"CLIENT_CHANNEL_UNAVAILABLE","Este canal nao permite enviar anexos. Fale com a loja.");
        }
        if (file.Length is <= 0 or > 10485760) throw new DeliveryDomainException(422, "CHAT_FILE_INVALID", "Arquivo deve ter ate 10 MB.");
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        var content = buffer.ToArray();
        var type = DetectType(content);
        if (type == null) throw new DeliveryDomainException(422, "CHAT_FILE_INVALID", "Formato de imagem ou audio nao permitido.");
        var name = Path.GetFileName(file.FileName);
        if (name.Length > 120) name = name[^120..];
        return await repository.SaveFileAsync(actor, thread, name, type, content);
    }
    internal static string? DetectType(byte[] data)
    {
        if (data.Length < 12) return null;
        var b = data.AsSpan();
        if (b[..8].SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 })) return "image/png";
        if (b[0] == 255 && b[1] == 216 && b[2] == 255) return "image/jpeg";
        if (Encoding.ASCII.GetString(b[..4]) == "RIFF")
        {
            if (Encoding.ASCII.GetString(b.Slice(8,4)) == "WEBP") return "image/webp";
            if (Encoding.ASCII.GetString(b.Slice(8,4)) == "WAVE") return "audio/wav";
        }
        if (Encoding.ASCII.GetString(b[..4]) == "OggS") return "audio/ogg";
        if (Encoding.ASCII.GetString(b[..3]) == "ID3" || b[0] == 255 && (b[1] & 224) == 224) return "audio/mpeg";
        if (Encoding.ASCII.GetString(b.Slice(4,4)) == "ftyp" && Encoding.ASCII.GetString(b.Slice(8,4)) is "M4A " or "isom" or "mp42") return "audio/mp4";
        if (b[..4].SequenceEqual(new byte[] { 26,69,223,163 })) return "audio/webm";
        return null;
    }
    public async Task<CommunicationRepository.FileRow> FileAsync(ChatActor actor, Guid id)
    {
        var file = await repository.FileAsync(actor.EstablishmentId, id) ?? throw new DeliveryDomainException(404, "CHAT_FILE_NOT_FOUND", "Anexo nao encontrado.");
        var parts = file.ThreadKey.Split(':');
        var channel = parts[0];
        int? target = null;
        if (channel == "store") target = int.Parse(parts[1]);
        if (channel == "client") target = int.Parse(parts[1]);
        if (channel == "private")
        {
            if (!actor.MotoboyId.HasValue || actor.MotoboyId.Value.ToString() != parts[1] && actor.MotoboyId.Value.ToString() != parts[2]) throw new DeliveryDomainException(403, "CHAT_FILE_FORBIDDEN", "Anexo indisponivel.");
            target = int.Parse(parts[actor.MotoboyId.Value.ToString() == parts[1] ? 2 : 1]);
        }
        var thread = await AuthorizeAsync(actor, channel, target);
        if (thread.Key != file.ThreadKey || file.OwnerKey != actor.Key && !await repository.FilePublishedAsync(actor.EstablishmentId, id)) throw new DeliveryDomainException(403, "CHAT_FILE_FORBIDDEN", "Anexo indisponivel.");
        return file;
    }
    public async Task<IReadOnlyList<CommunicationMessageDto>> NotificationsAsync(ChatActor actor)
    {
        await AuthorizeAsync(actor, "group", null);
        return await repository.NotificationsAsync(actor);
    }
    public async Task DismissAsync(ChatActor actor, Guid id)
    {
        await AuthorizeAsync(actor, "group", null);
        await repository.DismissAsync(actor, id);
    }
}
