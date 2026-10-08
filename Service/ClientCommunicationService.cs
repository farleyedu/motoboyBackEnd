using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Services;
using APIBack.DTOs.Atendimento;
using APIBack.Repository;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;

namespace APIBack.Service;

public sealed class ClientCommunicationService(ClientCommunicationRepository repository,CommunicationService communication,CommunicationRepository files,
    IAtendimentoRepository channels,IPedidoQueueService queues,IMessageRepository messages,ConversationManagementService management,WhatsAppSender whatsapp)
{
    private async Task<PedidoCanalDto> ChannelAsync(ChatActor actor,int pedido,bool send)
    {
        if(!actor.MotoboyId.HasValue)throw new DeliveryDomainException(403,"OPERATIONAL_TOKEN_REQUIRED","Use o acesso do motoboy.");
        var queue=await queues.GetQueueAsync(actor.EstablishmentId,actor.MotoboyId.Value);
        if(!AtendimentoService.PedidoEstaNaFila(queue,pedido))throw new DeliveryDomainException(403,"PEDIDO_NOT_IN_YOUR_QUEUE","Este pedido nao esta na sua fila.");
        var channel=await channels.GetCanalDoPedidoAsync(actor.EstablishmentId,pedido);
        if(send&&(!channel.PodeReceber||!channel.ConversaId.HasValue))throw new DeliveryDomainException(422,"CLIENT_CHANNEL_UNAVAILABLE","Este canal nao permite envio. Fale com a loja.");
        return channel;
    }
    internal static (DateTime? Before,Guid BeforeId) Cursor(string? before)
    {
        if(string.IsNullOrWhiteSpace(before))return (null,Guid.Empty);
        var parts=before.Split('|');
        if(parts.Length>2||!DateTimeOffset.TryParse(parts[0],System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal,out var date)
            ||parts.Length==2&&!Guid.TryParse(parts[1],out _))throw new DeliveryDomainException(422,"CHAT_CURSOR_INVALID","Cursor de historico invalido.");
        return(date.UtcDateTime,parts.Length==2?Guid.Parse(parts[1]):Guid.Empty);
    }
    public async Task<MotoboyClientChatDto> ListAsync(ChatActor actor,int pedido,string? before,int limit,string? search=null)
    {
        var channel=await ChannelAsync(actor,pedido,false);
        var result=new MotoboyClientChatDto{Channel=channel};
        if(!channel.ConversaId.HasValue)return result;
        var cursor=Cursor(before);
        var sender=AtendimentoService.BuildCriadaPorLabel(await channels.ObterNomeMotoboyAsync(actor.MotoboyId!.Value));
        search=string.IsNullOrWhiteSpace(search)?null:search.Trim()[..Math.Min(search.Trim().Length,100)];
        var page=await repository.HistoryAsync(actor.EstablishmentId,channel.ConversaId.Value,sender,cursor.Before,cursor.BeforeId,search,Math.Clamp(limit,1,100));
        result.Messages=page.Messages;
        result.HasMore=page.More;
        var first=page.Messages.FirstOrDefault();
        result.Cursor=first==null?null:$"{DateTime.SpecifyKind(first.CreatedAtUtc,DateTimeKind.Utc):O}|{first.Id}";
        var data=await repository.MetadataAsync(actor.EstablishmentId,result.Messages.Select(m=>m.Id).ToArray());
        var reactions=await repository.ReactionsAsync(actor,result.Messages.Select(m=>m.Id).ToArray());
        foreach(var message in result.Messages)
        {
            if(reactions.TryGetValue(message.Id,out var reaction))message.Reactions=reaction;
            var incoming=(ClientCommunicationRepository.MessageRow)message;
            message.Attachment=IncomingAttachment(incoming,pedido);
            if(!data.TryGetValue(message.Id,out var metadata))continue;
            message.ReplyTo=metadata.ReplyTo;
            if(metadata.AttachmentId.HasValue)
            {
                var file=await files.FileAsync(actor.EstablishmentId,metadata.AttachmentId.Value);
                if(file!=null)message.Attachment=new(){Id=file.Id,Name=file.Name,ContentType=file.ContentType,Size=file.Size};
            }
        }
        return result;
    }
    internal static CommunicationAttachmentDto? IncomingAttachment(ClientCommunicationRepository.MessageRow message,int pedido)
    {
        if(string.IsNullOrWhiteSpace(message.IncomingMedia))return null;
        try
        {
            using var json=JsonDocument.Parse(message.IncomingMedia);
            var media=json.RootElement;
            if(media.ValueKind!=JsonValueKind.Object)return null;
            if(!media.TryGetProperty("id",out var id)||string.IsNullOrWhiteSpace(id.GetString()))return null;
            var type=media.TryGetProperty("mime_type",out var mime)?mime.GetString():null;
            if(type==null||!(type.StartsWith("image/")||type.StartsWith("audio/")))return null;
            if(media.TryGetProperty("caption",out var caption)&&caption.ValueKind==JsonValueKind.String)message.Body=caption.GetString()??"";
            else message.Body="";
            return new(){Id=message.Id,ClientPedidoId=pedido,Name=type.StartsWith("image/")?"Foto recebida":"Audio recebido",ContentType=type,Size=0};
        }
        catch(JsonException){return null;}
    }
    public async Task<(byte[] Content,string Type)> DownloadAsync(ChatActor actor,int pedido,Guid id,CancellationToken ct)
    {
        var channel=await ChannelAsync(actor,pedido,false);
        if(!channel.ConversaId.HasValue)throw new DeliveryDomainException(404,"CHAT_ATTACHMENT_INVALID","Anexo indisponivel.");
        var message=await repository.MessageAsync(actor.EstablishmentId,channel.ConversaId.Value,"",id);
        if(message==null||IncomingAttachment(message,pedido)==null||string.IsNullOrWhiteSpace(message.PhoneNumberId))throw new DeliveryDomainException(404,"CHAT_ATTACHMENT_INVALID","Anexo indisponivel nesta conversa.");
        using var json=JsonDocument.Parse(message.IncomingMedia!);
        return await whatsapp.DownloadOperationalMediaAsync(actor.EstablishmentId,message.PhoneNumberId,json.RootElement.GetProperty("id").GetString()!,ct);
    }
    public async Task ReactAsync(ChatActor actor,int pedido,Guid id,string? reaction)
    {
        var emoji=reaction switch{null=>"","like"=>"\U0001F44D","heart"=>"\u2764\uFE0F","thanks"=>"\U0001F64F","alert"=>"\u26A0\uFE0F",_=>throw new DeliveryDomainException(422,"CHAT_REACTION_INVALID","Reacao invalida.")};
        var channel=await ChannelAsync(actor,pedido,true);
        var message=await repository.MessageAsync(actor.EstablishmentId,channel.ConversaId!.Value,"",id);
        if(string.IsNullOrWhiteSpace(message?.ProviderId))throw new DeliveryDomainException(422,"CHAT_REACTION_INVALID","Esta mensagem nao permite reacao.");
        try{await management.SendOperationalReactionAsync(channel.ConversaId.Value,actor.EstablishmentId,message.ProviderId,emoji);}
        catch(ConversationManagementException ex){throw new DeliveryDomainException(ex.StatusCode,ex.Code??"CHAT_REACTION_FAILED",ex.Message);}
        await repository.SetReactionAsync(actor,id,reaction);
    }
    public async Task<CommunicationMessageDto> SendAsync(ChatActor actor,int pedido,SendCommunicationRequest request)
    {
        CommunicationService.Validate(request);
        if(request.Mentions.Length>0)throw new DeliveryDomainException(422,"CHAT_MENTION_INVALID","Mencoes da equipe nao sao enviadas ao cliente.");
        var channel=await ChannelAsync(actor,pedido,true);
        CommunicationRepository.FileRow? file=null;
        if(request.AttachmentId.HasValue)
        {
            file=await communication.FileAsync(actor,request.AttachmentId.Value);
            if(file.ThreadKey!=$"client:{pedido}"||file.OwnerKey!=actor.Key)throw new DeliveryDomainException(422,"CHAT_ATTACHMENT_INVALID","Anexo de outra conversa.");
            if(file.ContentType is not ("image/jpeg" or "image/png" or "audio/mp4" or "audio/mpeg" or "audio/ogg"))throw new DeliveryDomainException(422,"CLIENT_MEDIA_UNSUPPORTED","Este formato nao e aceito pelo WhatsApp. Grave o audio no aplicativo Android.");
            if(file.ContentType.StartsWith("audio/")&&!string.IsNullOrEmpty(request.Body))throw new DeliveryDomainException(422,"CLIENT_AUDIO_CAPTION_UNSUPPORTED","Envie o texto em uma mensagem separada do audio.");
        }
        string? replyProvider=null;
        if(request.ReplyTo.HasValue)
        {
            if(await messages.ObterConversaDaMensagemAsync(request.ReplyTo.Value)!=channel.ConversaId)throw new DeliveryDomainException(422,"CHAT_REPLY_INVALID","A mensagem citada nao pertence a esta conversa.");
            replyProvider=(await messages.GetByConversationAsync(channel.ConversaId!.Value,1000)).FirstOrDefault(m=>m.Id==request.ReplyTo)?.IdProvedor;
            if(string.IsNullOrWhiteSpace(replyProvider))throw new DeliveryDomainException(422,"CHAT_REPLY_INVALID","Esta mensagem ainda nao pode ser citada no WhatsApp.");
        }
        var name=AtendimentoService.BuildCriadaPorLabel(await channels.ObterNomeMotoboyAsync(actor.MotoboyId!.Value));
        var fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{Pedido=pedido,request.Body,request.AttachmentId,request.ReplyTo}))));
        var prior=await repository.ClaimAsync(actor,pedido,request,fingerprint);
        Guid id;
        if(prior!=null)id=prior.MessageId!.Value;
        else
        {
            try { id=await management.SendOperationalMessageAsync(channel.ConversaId!.Value,actor.EstablishmentId,request.Body!,name,file?.Content,file?.ContentType,replyProvider);await repository.FinishAsync(actor,request.ClientId,id); }
            catch(Exception ex)
            {
                await repository.FinishAsync(actor,request.ClientId,null);
                if(ex is ConversationManagementException error)throw new DeliveryDomainException(error.StatusCode,error.Code??"CHAT_SEND_UNCERTAIN",error.Message);
                throw;
            }
        }
        return new(){Id=id,ClientId=request.ClientId,Channel="client",ThreadKey=$"client:{pedido}",Body=request.Body!,Mine=true,SenderName=name,CreatedAtUtc=prior?.CreatedAtUtc??DateTimeOffset.UtcNow,
            Attachment=file==null?null:new(){Id=file.Id,Name=file.Name,ContentType=file.ContentType,Size=file.Size},ReplyTo=request.ReplyTo};
    }
}
