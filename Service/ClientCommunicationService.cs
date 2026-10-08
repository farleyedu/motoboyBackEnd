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
    AtendimentoService atendimento,IAtendimentoRepository channels,IPedidoQueueService queues,IMessageRepository messages,ConversationManagementService management)
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
    public async Task<MotoboyClientChatDto> ListAsync(ChatActor actor,int pedido,DateTime? before,int limit)
    {
        var result=await atendimento.ListClientMessagesAsync(actor.EstablishmentId,actor.MotoboyId!.Value,pedido,before,limit);
        var data=await repository.MetadataAsync(actor.EstablishmentId,result.Messages.Select(m=>m.Id).ToArray());
        foreach(var message in result.Messages)
        {
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
