using System.Net;
using System.Text;
using APIBack.Atendimento;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Services;
using APIBack.DTOs.Atendimento;
using APIBack.DTOs.Delivery;
using APIBack.Repository.Interface;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class CommunicationMediaTests
{
    [Fact]
    public async Task CommunicationClientDownloadAndReactionRejectOrderOutsideOwnQueueBeforeTransport()
    {
        var est=Guid.NewGuid();var queues=new Mock<IPedidoQueueService>();
        queues.Setup(q=>q.GetQueueAsync(est,1,It.IsAny<CancellationToken>())).ReturnsAsync(new MotoboyQueueDto());
        var service=new ClientCommunicationService(null!,null!,null!,Mock.Of<IAtendimentoRepository>(),queues.Object,null!,null!,null!);
        var actor=new ChatActor(est,7,1);
        Assert.Equal(403,(await Assert.ThrowsAsync<DeliveryDomainException>(()=>service.DownloadAsync(actor,23,Guid.NewGuid(),default))).StatusCode);
        Assert.Equal(403,(await Assert.ThrowsAsync<DeliveryDomainException>(()=>service.ReactAsync(actor,23,Guid.NewGuid(),"like"))).StatusCode);
    }
    [Fact]
    public async Task CommunicationIncomingMediaUsesScopedBearerAndChecksDownloadedBytes()
    {
        var est=Guid.NewGuid();var canals=new Mock<ICanalRepository>();
        canals.Setup(c=>c.ObterAtivoPorPhoneNumberIdAsync("111")).ReturnsAsync(new CanalWhatsapp{IdEstabelecimento=est,TokenCifrado="encrypted"});
        var protector=new Mock<ITokenProtector>();protector.Setup(p=>p.Revelar("encrypted")).Returns("private-token");
        var calls=new List<Uri>();
        var bytes=new byte[20];new byte[]{137,80,78,71,13,10,26,10}.CopyTo(bytes,0);
        var factory=new Mock<IHttpClientFactory>();
        factory.Setup(f=>f.CreateClient("chat-media")).Returns(()=>new HttpClient(new Handler(request=>{
            calls.Add(request.RequestUri!);Assert.Equal("private-token",request.Headers.Authorization?.Parameter);
            return calls.Count==1?new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"url\":\"https://lookaside.fbsbx.com/whatsapp_business/attachments\"}")}
                :new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};
        })));
        var sender=new WhatsAppSender(factory.Object,Mock.Of<IWhatsAppTokenProvider>(),canals.Object,protector.Object,Mock.Of<IMessageService>(),new ConfigurationBuilder().Build(),NullLogger<WhatsAppSender>.Instance,Mock.Of<ISimulatedCustomerGuard>());
        var file=await sender.DownloadOperationalMediaAsync(est,"111","123456",default);
        Assert.Equal("image/png",file.Type);Assert.Equal(bytes,file.Content);Assert.Equal(2,calls.Count);
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>sender.DownloadOperationalMediaAsync(Guid.NewGuid(),"111","123456",default));Assert.Equal(2,calls.Count);
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(send(request));
    }
}
