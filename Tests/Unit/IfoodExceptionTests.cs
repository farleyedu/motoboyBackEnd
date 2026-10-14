using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using APIBack.Controllers;
using APIBack.DTOs.Delivery;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Commands;
using APIBack.Integrations.Ifood.Exceptions;
using APIBack.Service;
using Moq;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class IfoodExceptionTests
{
    internal static readonly Guid RefundId=Guid.Parse("10000000-0000-0000-0000-000000000001");
    internal static readonly Guid TimeId=Guid.Parse("10000000-0000-0000-0000-000000000002");
    internal static JsonElement DisputePayload(Guid dispute,string type="AFTER_DELIVERY",string action="CANCELLATION")=>JsonSerializer.SerializeToElement(new
    {
        disputeId=dispute,handshakeType=type,action,expiresAt="2026-10-13T18:10:00Z",timeoutAction="REJECT_CANCELLATION",
        metadata=new { acceptCancellationReasons=type=="DELAY"?new[] { "STORE_SYSTEM_ISSUES" }:Array.Empty<string>() },
        alternatives=new object[]
        {
            new { id=RefundId,type="REFUND",metadata=new { maxAmount=new { value="05000",currency="BRL" } } },
            new { id=TimeId,type="ADDITIONAL_TIME",metadata=new { allowedsAdditionalTimeInMinutes=new[] { 10,20,30 },allowedsAdditionalTimeReasons=new[] { "OPERATIONAL_ISSUES" } } }
        }
    });
    internal static IfoodDisputeContext Dispute(string type="AFTER_DELIVERY")
    {
        var id=Guid.NewGuid();return new() { DisputeId=id,Revision=1,State="open",ExpiresAtUtc=DateTimeOffset.Parse("2026-10-13T18:10:00Z"),TimeoutAction="REJECT_CANCELLATION",PayloadJson=DisputePayload(id,type).GetRawText() };
    }
    internal static IfoodOptions Options(Guid merchant,Guid store)
    {
        var options=IfoodAuthenticationTests.EnabledOptions();options.ExceptionsEnabled=true;
        options.ExceptionMerchants.Add(new() { MerchantId=merchant,EstabelecimentoId=store });
        options.AllowedExceptionActions.AddRange(new[] { "request_cancellation","dispute_accept","dispute_reject","dispute_alternative" });return options;
    }
    internal static IfoodDisputeResponseRequest Response(string action="accept")=>new() { OperationId=Guid.NewGuid(),ExpectedRevision=2,ExpectedDisputeRevision=1,Action=action };
    [Theory]
    [InlineData("[{\"code\":\"00501\",\"description\":\"Motivo atual\"}]","00501")]
    [InlineData("{\"reasons\":[{\"code\":\"NEW_CODE\",\"description\":\"Motivo novo\"}]}","NEW_CODE")]
    [InlineData("[{\"cancelCodeId\":\"00502\",\"description\":\"Outro motivo\",\"secret\":\"not-copied\"}]","00502")]
    public void ReasonsAreDynamicStringsWithLeadingZerosAndNoFixedFallback(string json,string code)
    {
        var result=IfoodCancellationReasonsClient.Parse(json);Assert.Equal(code,Assert.Single(result).Code);Assert.DoesNotContain("not-copied",JsonSerializer.Serialize(result));
        Assert.Empty(IfoodCancellationReasonsClient.Parse("[]"));
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("[{\"code\":501,\"description\":\"Motivo\"}]")]
    [InlineData("[{\"code\":\"501\",\"cancelCodeId\":\"502\",\"description\":\"Motivo\"}]")]
    [InlineData("[{\"code\":\"501\",\"description\":\"A\"},{\"code\":\"501\",\"description\":\"B\"}]")]
    [InlineData("[{\"code\":\" 501\",\"description\":\"A\"}]")]
    public void InvalidReasonsDoNotBecomeHardcodedOrLossyCodeList(string json)
        =>Assert.Equal("IFOOD_CANCELLATION_REASONS_INVALID",Assert.Throws<IfoodIntegrationException>(()=>IfoodCancellationReasonsClient.Parse(json)).Code);
    [Fact]
    public void DisputeOptionsDependOnHandshakeAndPayloadAndNeverExposeEvidenceUrls()
    {
        var dispute=Dispute("DELAY");var options=IfoodExceptionRules.Options(dispute,2,new IfoodCommandTests.Clock().Now);
        Assert.True(options.CanRespond);Assert.Contains("accept",options.Actions);Assert.Equal("STORE_SYSTEM_ISSUES",Assert.Single(options.AcceptReasons));
        Assert.Equal(2,options.Alternatives.Count);Assert.Equal("05000",options.Alternatives[0].MaxAmountMinor);
        Assert.DoesNotContain("PayloadJson",JsonSerializer.Serialize(options));
        dispute.PayloadJson=DisputePayload(dispute.DisputeId,"FUTURE_TYPE").GetRawText();Assert.Empty(IfoodExceptionRules.Options(dispute,2,new IfoodCommandTests.Clock().Now).Actions);
    }
    [Theory]
    [InlineData("accept")]
    [InlineData("reject")]
    public void AcceptAndRejectUseApplicableContractAndExplicitReason(string action)
    {
        var dispute=Dispute();var request=Response(action);if(action=="reject")request.Reason="Conferimos os itens";
        var body=IfoodExceptionRules.Response(dispute,request,new IfoodCommandTests.Clock().Now);
        if(action=="accept")Assert.Equal("{}",body.GetRawText());else Assert.Equal(request.Reason,body.GetProperty("reason").GetString());
        if(action=="reject") { request.Reason=null;Assert.Throws<DeliveryDomainException>(()=>IfoodExceptionRules.Response(dispute,request,new IfoodCommandTests.Clock().Now)); }
    }
    [Theory]
    [InlineData("expired")]
    [InlineData("stale")]
    [InlineData("pending")]
    [InlineData("closed")]
    public void ExpiredChangedClosedAndAlreadyAnsweredDisputeDoNotSendAnotherResponse(string condition)
    {
        var dispute=Dispute();var now=new IfoodCommandTests.Clock().Now;var request=Response();
        if(condition=="expired")now=dispute.ExpiresAtUtc;
        if(condition=="stale")request.ExpectedDisputeRevision=2;
        if(condition=="pending")dispute.ResponseOperationId=Guid.NewGuid();
        if(condition=="closed")dispute.State="resolved";
        Assert.Throws<DeliveryDomainException>(()=>IfoodExceptionRules.Response(dispute,request,now));
    }
    [Fact]
    public void DelayAcceptRequiresReasonFromPayloadAndTimeMustBeOneOfAuthorizedChoices()
    {
        var dispute=Dispute("DELAY");var request=Response();
        Assert.Throws<DeliveryDomainException>(()=>IfoodExceptionRules.Response(dispute,request,new IfoodCommandTests.Clock().Now));
        request.Reason="STORE_SYSTEM_ISSUES";IfoodExceptionRules.Response(dispute,request,new IfoodCommandTests.Clock().Now);
        request=Response("alternative");request.AlternativeId=TimeId;request.AdditionalTimeInMinutes=20;request.Reason="OPERATIONAL_ISSUES";
        var body=IfoodExceptionRules.Response(dispute,request,new IfoodCommandTests.Clock().Now);Assert.Equal(20,body.GetProperty("metadata").GetProperty("additionalTimeInMinutes").GetInt32());
        Assert.Equal(request.Reason,body.GetProperty("metadata").GetProperty("additionalTimeReason").GetString());
        request.AdditionalTimeInMinutes=25;Assert.Throws<DeliveryDomainException>(()=>IfoodExceptionRules.Response(dispute,request,new IfoodCommandTests.Clock().Now));
    }
    [Theory]
    [InlineData("0200","BRL",true)]
    [InlineData("5001","BRL",false)]
    [InlineData("2.00","BRL",false)]
    [InlineData("200","USD",false)]
    [InlineData("0","BRL",false)]
    [InlineData("-1","BRL",false)]
    public void RefundUsesIntegerMinorUnitStringCurrencyAndAuthorizedMaximum(string amount,string currency,bool allowed)
    {
        var request=Response("alternative");request.AlternativeId=RefundId;request.AmountMinor=amount;request.Currency=currency;var dispute=Dispute();
        if(allowed)Assert.Equal(amount,IfoodExceptionRules.Response(dispute,request,new IfoodCommandTests.Clock().Now).GetProperty("metadata").GetProperty("amount").GetProperty("value").GetString());
        else Assert.Throws<DeliveryDomainException>(()=>IfoodExceptionRules.Response(dispute,request,new IfoodCommandTests.Clock().Now));
    }
    [Theory]
    [InlineData("request_cancellation",202,"accepted")]
    [InlineData("dispute_accept",201,"accepted")]
    [InlineData("dispute_reject",201,"accepted")]
    [InlineData("dispute_alternative",201,"accepted")]
    [InlineData("dispute_accept",422,"review")]
    [InlineData("dispute_reject",500,"uncertain")]
    public async Task ExceptionClientUsesBoundOrderOrDisputePathAndWaitsForEvent(string command,int status,string state)
    {
        var dispute=Dispute();var request=Response(command=="dispute_reject"?"reject":command=="dispute_alternative"?"alternative":"accept");
        if(command=="dispute_reject")request.Reason="Itens conferidos";
        if(command=="dispute_alternative") { request.AlternativeId=RefundId;request.AmountMinor="0200";request.Currency="BRL"; }
        var body=command=="request_cancellation"?JsonSerializer.SerializeToElement(new { reason="00501" }):IfoodExceptionRules.Response(dispute,request,new IfoodCommandTests.Clock().Now);
        var item=new IfoodCommandReservation { Command=command,MerchantId=Guid.NewGuid(),ExternalOrderId=Guid.NewGuid(),EstabelecimentoId=Guid.NewGuid(),PayloadJson=JsonSerializer.Serialize(new IfoodExceptionIntent { DisputeId=dispute.DisputeId,AlternativeId=request.AlternativeId,Response=request,Body=body }) };
        var durable=false;var handler=new IfoodAuthenticationTests.Handler { Send=async(http,ct)=>
        {
            Assert.True(durable);Assert.Equal(HttpMethod.Post,http.Method);
            Assert.Equal(IfoodExceptionRules.Endpoint(item,IfoodExceptionRules.Intent(item.PayloadJson)),http.RequestUri);
            var sent=await http.Content!.ReadAsStringAsync(ct);Assert.DoesNotContain("DisputeRevision",sent);Assert.DoesNotContain("OperationId",sent);
            return new HttpResponseMessage((HttpStatusCode)status) { Content=new StringContent("secret-error-body") };
        } };
        var result=await IfoodCommandTests.Client(handler,item,out _).SendAsync(item,_=>{durable=true;return Task.CompletedTask;},default);
        Assert.Equal(state,result.State);Assert.Equal(1,handler.Calls);Assert.DoesNotContain("secret-error-body",result.ToString());
    }
    [Fact]
    public async Task DisabledExceptionServiceDoesNotAccessDatabaseAndEveryEndpointHasIntegrationPermission()
    {
        await using var source=NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused");var options=Microsoft.Extensions.Options.Options.Create(new IfoodOptions());
        var service=new IfoodExceptionService(new(source,options,new IfoodCommandTests.Clock()),new Mock<IIfoodCommandClient>().Object,new Mock<IIfoodCancellationReasonsClient>().Object,options,new IfoodCommandTests.Clock());
        Assert.Equal(0,await service.RunOnceAsync());await Assert.ThrowsAsync<DeliveryDomainException>(()=>service.ReasonsAsync(Guid.NewGuid(),100));
        foreach(var name in new[] { "Reasons","Cancel","Options","Respond" })Assert.NotEmpty(typeof(IfoodExceptionsController).GetMethod(name)!.GetCustomAttributes(typeof(APIBack.Attributes.RequirePermissionAttribute),true));
        var configured=Options(Guid.NewGuid(),Guid.NewGuid());Assert.True(new IfoodOptionsValidator().Validate(null,configured).Succeeded);
        configured.ExceptionMerchants.Clear();Assert.True(new IfoodOptionsValidator().Validate(null,configured).Failed);
    }
}
