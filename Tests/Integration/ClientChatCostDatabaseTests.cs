using APIBack.Repository;
using APIBack.Service;
using Xunit;
using APIBack.DTOs.Atendimento;
using APIBack.Repository.Interface;
using Moq;

namespace APIBack.Tests.Integration;

public sealed partial class CommunicationDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task LightweightQueueCheckExcludesOtherRiderTenantUnacceptedOfferAndFinishedOrder()
    {
        await using var db=await Fixture.Create();var channels=new AtendimentoRepository(db.Source);
        await db.Execute("CREATE TABLE delivery_route_stops(estabelecimento_id uuid,motoboy_id integer,pedido_id integer,stop_status text,offered_at_utc timestamptz); INSERT INTO delivery_route_stops VALUES(@Est,1,23,'assigned',NULL)",new{db.Est});
        Assert.True(await channels.PedidoEstaNaFilaAsync(db.Est,1,23));
        Assert.False(await channels.PedidoEstaNaFilaAsync(db.Est,2,23));
        Assert.False(await channels.PedidoEstaNaFilaAsync(Guid.NewGuid(),1,23));
        await db.Execute("UPDATE delivery_route_stops SET offered_at_utc=NOW()");
        Assert.False(await channels.PedidoEstaNaFilaAsync(db.Est,1,23));
        var service=new ClientCommunicationService(null!,null!,null!,channels,null!,null!,null!);
        Assert.Equal("PEDIDO_NOT_IN_YOUR_QUEUE",(await Assert.ThrowsAsync<DeliveryDomainException>(()=>service.DownloadAsync(new(db.Est,7,1),23,Guid.NewGuid(),default))).Code);
        await db.Execute("UPDATE delivery_route_stops SET offered_at_utc=NULL,stop_status='en_route'");
        Assert.True(await channels.PedidoEstaNaFilaAsync(db.Est,1,23));
        await db.Execute("UPDATE delivery_route_stops SET stop_status='completed'");
        Assert.False(await channels.PedidoEstaNaFilaAsync(db.Est,1,23));
    }

    [DeliveryDatabaseFact]
    public async Task AttachmentBatchOnlyReturnsMetadataForCurrentTenant()
    {
        await using var db=await Fixture.Create();var files=new CommunicationRepository(db.Source);
        var ids=new List<Guid>();
        for(var i=0;i<20;i++)ids.Add((await files.SaveFileAsync(new(db.Est,7,1),new("client","client:23",null),"foto.png","image/png",new byte[100])).Id);
        var batch=await files.FileMetadataAsync(db.Est,ids.Append(Guid.NewGuid()).ToArray());
        Assert.Equal(20,batch.Count);Assert.All(batch.Values,f=>Assert.Equal(100,f.Size));
        Assert.Empty(await files.FileMetadataAsync(Guid.NewGuid(),ids.ToArray()));
        Assert.Empty(await files.FileMetadataAsync(db.Est,Array.Empty<Guid>()));
    }

    [DeliveryDatabaseFact]
    public async Task ClientListKeepsAuthorshipAndResolvesTwentyAttachmentsWithoutLoadingTheirBytes()
    {
        await using var db=await Fixture.Create();var files=new CommunicationRepository(db.Source);
        var history=new ClientCommunicationRepository(db.Source);var conversation=Guid.NewGuid();
        await db.Execute("""
CREATE TABLE conversas(id uuid PRIMARY KEY,id_cliente uuid,id_estabelecimento uuid);
CREATE TABLE mensagens(id uuid PRIMARY KEY,id_conversa uuid,conteudo text,tipo text,status text,criada_por text,id_provedor text,data_criacao timestamptz);
CREATE TABLE wa_evento(tipo text,chave text,payload jsonb,phone_number_id text,recebido_em timestamptz DEFAULT NOW());
INSERT INTO conversas VALUES(@Conversation,gen_random_uuid(),@Est);
""",new{Conversation=conversation,db.Est});
        var actor=new ChatActor(db.Est,7,1);
        for(var i=0;i<20;i++)
        {
            var id=Guid.NewGuid();var file=await files.SaveFileAsync(actor,new("client","client:23",null),"foto.png","image/png",new byte[100]);
            await db.Execute("INSERT INTO mensagens VALUES(@Id,@Conversation,'','imagem','enviada','Motoboy Alice',@Provider,NOW())",new{Id=id,Conversation=conversation,Provider=$"message-{i}"});
            var request=new SendCommunicationRequest{ClientId=Guid.NewGuid(),Body="",AttachmentId=file.Id};
            await history.ClaimAsync(actor,23,request,$"hash-{i}");await history.FinishAsync(actor,request.ClientId,id);
        }
        var channels=new Mock<IAtendimentoRepository>();
        channels.Setup(c=>c.PedidoEstaNaFilaAsync(db.Est,1,23)).ReturnsAsync(true);
        channels.Setup(c=>c.GetCanalDoPedidoAsync(db.Est,23)).ReturnsAsync(new PedidoCanalDto{ConversaId=conversation});
        channels.Setup(c=>c.ObterNomeMotoboyAsync(1)).ReturnsAsync("Alice");
        var service=new ClientCommunicationService(history,null!,files,channels.Object,null!,null!,null!);
        var page=await service.ListAsync(actor,23,null,20);
        Assert.Equal(20,page.Messages.Count);Assert.False(page.HasMore);
        Assert.All(page.Messages,m=>{Assert.True(m.Mine);Assert.NotNull(m.Attachment);Assert.Equal(100,m.Attachment!.Size);});
    }
}
