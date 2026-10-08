using System.Text;
using APIBack.DTOs.Atendimento;
using APIBack.Service;
using Xunit;
using APIBack.Automation.Services;
using APIBack.Repository;

namespace APIBack.Tests.Unit;

public sealed class CommunicationTests
{
    private readonly Guid est=Guid.NewGuid();
    [Fact] public void ClientCursorRoundTripsAndRejectsMalformedInput()
    {
        var id=Guid.NewGuid();var cursor=ClientCommunicationService.Cursor($"2026-10-08T10:00:00Z|{id}");Assert.Equal(id,cursor.BeforeId);Assert.Equal(DateTimeKind.Utc,cursor.Before!.Value.Kind);
        Assert.Throws<DeliveryDomainException>(()=>ClientCommunicationService.Cursor("broken"));
        Assert.Throws<DeliveryDomainException>(()=>ClientCommunicationService.Cursor("2026-10-08|wrong"));
    }
    [Theory][InlineData("https://lookaside.fbsbx.com/whatsapp_business/attachments",true)][InlineData("https://media.fbcdn.net/file",true)]
    [InlineData("http://lookaside.fbsbx.com/file",false)][InlineData("https://lookaside.fbsbx.com.evil.test/file",false)]
    [InlineData("https://127.0.0.1/file",false)][InlineData("https://user@lookaside.fbsbx.com/file",false)]
    public void ClientMediaDownloadRestrictsHosts(string url,bool allowed)=>Assert.Equal(allowed,WhatsAppSender.AllowedMediaUrl(new Uri(url)));
    [Fact] public void IncomingAudioAndPhotoExposeProtectedDescriptorNotProviderUrl()
    {
        var row=new ClientCommunicationRepository.MessageRow{Id=Guid.NewGuid(),Body="placeholder",IncomingMedia="{\"id\":\"123456\",\"mime_type\":\"audio/ogg\"}"};
        var file=ClientCommunicationService.IncomingAttachment(row,23);Assert.Equal(row.Id,file!.Id);Assert.Equal(23,file.ClientPedidoId);Assert.Equal("",row.Body);
        row.IncomingMedia="{}";Assert.Null(ClientCommunicationService.IncomingAttachment(row,23));
    }
    [Fact] public void PrivateThreadIsSymmetricAndAdminCannotReadIt()
    {
        Assert.Equal(CommunicationService.Resolve(new(est,7,1),"private",2).Key,CommunicationService.Resolve(new(est,8,2),"private",1).Key);
        Assert.Throws<DeliveryDomainException>(()=>CommunicationService.Resolve(new(est,7,null),"private",2));
        Assert.Throws<DeliveryDomainException>(()=>CommunicationService.Resolve(new(est,7,1),"private",1));
    }
    [Fact] public void MobileStoreTargetAlwaysComesFromActor()
    {
        Assert.Equal("store:1",CommunicationService.Resolve(new(est,7,1),"store",99).Key);
        Assert.Equal("store:2",CommunicationService.Resolve(new(est,7,null),"store",2).Key);
    }
    [Theory][InlineData(0)][InlineData(-1)] public void InvalidTargetsAreRejected(int target)=>Assert.Throws<DeliveryDomainException>(()=>CommunicationService.Resolve(new(est,7,1),"private",target));
    [Fact] public void RequestRequiresIdAndTextOrAttachment()
    {
        Assert.Throws<DeliveryDomainException>(()=>CommunicationService.Validate(new(){Body="oi"}));
        Assert.Throws<DeliveryDomainException>(()=>CommunicationService.Validate(new(){ClientId=Guid.NewGuid(),Body="  "}));
        CommunicationService.Validate(new(){ClientId=Guid.NewGuid(),AttachmentId=Guid.NewGuid()});
    }
    [Fact] public void MentionsAreNormalizedAndInvalidIdsAreRejected()
    {
        var request=new SendCommunicationRequest{ClientId=Guid.NewGuid(),Body=" oi ",Mentions=new[]{3,2,3}};
        CommunicationService.Validate(request);Assert.Equal("oi",request.Body);Assert.Equal(new[]{2,3},request.Mentions);
        request.Mentions=new[]{-1};Assert.Throws<DeliveryDomainException>(()=>CommunicationService.Validate(request));
    }
    [Fact] public void LengthIsValidatedAgainstLegacyDatabaseLimit()=>Assert.Throws<DeliveryDomainException>(()=>CommunicationService.Validate(new(){ClientId=Guid.NewGuid(),Body=new string('a',501)}));
    [Fact] public void HtmlAndUnknownBytesAreNotAcceptedAsImages()
    {
        Assert.Null(CommunicationService.DetectType(Encoding.UTF8.GetBytes("<html>arquivo.png</html>")));
        Assert.Null(CommunicationService.DetectType(new byte[8]));
    }
    [Theory][InlineData("OggS","audio/ogg")][InlineData("ID3x","audio/mpeg")]
    public void AudioFormatComesFromBytesNotFilename(string signature,string expected)
    {
        var data=new byte[32];Encoding.ASCII.GetBytes(signature).CopyTo(data,0);Assert.Equal(expected,CommunicationService.DetectType(data));
    }
}
