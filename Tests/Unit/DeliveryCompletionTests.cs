using APIBack.DTOs.Delivery;
using APIBack.Service;
using Xunit;
namespace APIBack.Tests.Unit;
public class DeliveryCompletionTests
{
    private static DeliveryCompletionRequest Request(params DeliveryPaymentPart[] parts)=>new(){OperationId=Guid.NewGuid(),ExpectedPedidoId=23,ExpectedVersion=3,Payments=parts.ToList()};
    private static DeliveryPaymentPart Part(string method,decimal amount,decimal? cash=null,bool confirmed=true)=>new(){Method=method,Amount=amount,CashReceived=cash,ReceivedConfirmed=confirmed};
    [Fact] public void ExactSplitWithChangeIsValid()=>DeliveryCompletionRules.Validate(Request(Part("pix",20m),Part("dinheiro",66.9m,100m)),86.9m,true);
    [Fact] public void OversizedPartsAreRejectedBeforeDecimalOverflow()
    {
        foreach(var amount in new[]{decimal.MaxValue,decimal.MinValue})
            Assert.Equal(422,Assert.Throws<DeliveryDomainException>(()=>DeliveryCompletionRules.Validate(Request(Part("pix",amount),Part("credito",amount)),86.9m,true)).StatusCode);
    }
    [Theory][InlineData(86.89)][InlineData(86.91)][InlineData(0)] public void RejectsIncompleteTotal(decimal value)=>Assert.Equal("PAYMENT_TOTAL_MISMATCH",Assert.Throws<DeliveryDomainException>(()=>DeliveryCompletionRules.Validate(Request(Part("pix",value)),86.9m,true)).Code);
    [Fact] public void CashMustCoverItsOwnPart()=>Assert.Equal("CASH_INSUFFICIENT",Assert.Throws<DeliveryDomainException>(()=>DeliveryCompletionRules.Validate(Request(Part("dinheiro",86.9m,80m)),86.9m,true)).Code);
    [Fact] public void EachReceiptIsExplicit()=>Assert.Equal("PAYMENT_NOT_CONFIRMED",Assert.Throws<DeliveryDomainException>(()=>DeliveryCompletionRules.Validate(Request(Part("pix",86.9m,null,false)),86.9m,true)).Code);
    [Fact] public void AlreadyPaidDoesNotAcceptAnotherReceipt()=>Assert.Equal("PAYMENT_ALREADY_PAID",Assert.Throws<DeliveryDomainException>(()=>DeliveryCompletionRules.Validate(Request(Part("pix",86.9m)),86.9m,false)).Code);
    [Fact] public void CannotRoundAwayFractionalCent()=>Assert.Throws<DeliveryDomainException>(()=>DeliveryCompletionRules.Validate(Request(Part("pix",1.001m),Part("pix",1.999m)),3m,true));
    [Fact] public void MethodAndCashMustAgree()=>Assert.Equal("PAYMENT_INVALID",Assert.Throws<DeliveryDomainException>(()=>DeliveryCompletionRules.Validate(Request(Part("pix",86.9m,100m)),86.9m,true)).Code);
    [Fact] public void HashFencesReusedIdWithDifferentPayload(){var request=Request(Part("pix",86.9m));var hash=DeliveryCompletionRules.Hash(request);request.Codigo="1234";Assert.NotEqual(hash,DeliveryCompletionRules.Hash(request));Assert.Equal(64,hash.Length);}
    [Theory][InlineData("pago")][InlineData("Pago")][InlineData("approved")][InlineData("aprovado")] public void RecognizesPaid(string status)=>Assert.True(DeliveryCompletionRules.IsPaid(status));
    [Theory][InlineData("a_receber")][InlineData(null)] public void UnknownPaymentIsNeverAutomaticallyPaid(string? status)=>Assert.False(DeliveryCompletionRules.IsPaid(status));
    [Fact] public void PickupRejectsForeignList(){var r=new PickupStopsRequest{ExpectedPedidoId=23,ExpectedVersion=3,PedidoIds=new(){23,99}};Assert.Throws<DeliveryDomainException>(()=>DeliveryPickupRules.ValidateSnapshot(r,3,new[]{23,24},false));}
    [Fact] public void PickupRetryRequiresSameSet(){var r=new PickupStopsRequest{ExpectedPedidoId=23,ExpectedVersion=3,PedidoIds=new(){23,24}};DeliveryPickupRules.ValidateSnapshot(r,4,new[]{23,24},true);Assert.Throws<DeliveryDomainException>(()=>DeliveryPickupRules.ValidateSnapshot(r,4,new[]{23,24},false));}
    [Fact] public void InvalidPhotoIsRejected()=>Assert.Throws<ArgumentException>(()=>MotoboyContaService.SanitizeImage(Convert.ToBase64String(new byte[]{1,2,3,4}),false));
    [Theory][InlineData(1,false)][InlineData(2,false)][InlineData(3,false)][InlineData(4,false)][InlineData(5,true)][InlineData(6,true)][InlineData(7,true)][InlineData(8,true)]
    public void PrivatePhotoHonorsExifOrientationBeforeRemovingMetadata(byte orientation,bool swapped)
    {
        using var bitmap=new SkiaSharp.SKBitmap(32,24);using var canvas=new SkiaSharp.SKCanvas(bitmap);canvas.Clear(SkiaSharp.SKColors.CornflowerBlue);
        using var image=SkiaSharp.SKImage.FromBitmap(bitmap);using var jpg=image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg,90);
        var exif=new byte[]{69,120,105,102,0,0,73,73,42,0,8,0,0,0,1,0,18,1,3,0,1,0,0,0,orientation,0,0,0,0,0,0,0};
        var original=jpg.ToArray();var withExif=new byte[]{255,216,255,225,0,34}.Concat(exif).Concat(original.Skip(2)).ToArray();
        var sanitized=MotoboyContaService.SanitizeImage(Convert.ToBase64String(withExif),false);
        using var codec=SkiaSharp.SKCodec.Create(new SkiaSharp.SKMemoryStream(sanitized));
        Assert.Equal(swapped?24:32,codec.Info.Width);Assert.Equal(swapped?32:24,codec.Info.Height);Assert.Equal(SkiaSharp.SKEncodedOrigin.TopLeft,codec.EncodedOrigin);
    }
}
