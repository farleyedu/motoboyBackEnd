using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using APIBack.Controllers;
using APIBack.DTOs.Delivery;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Commands;
using APIBack.Integrations.Ifood.Orders;
using APIBack.Service;
using Moq;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class IfoodCommandTests
{
    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }=DateTimeOffset.Parse("2026-10-13T18:01:00Z");
        public override DateTimeOffset GetUtcNow()=>Now;
    }
    internal static IfoodOptions Options(Guid merchant,Guid store)
    {
        var options=IfoodAuthenticationTests.EnabledOptions();options.CommandsEnabled=true;
        options.CommandMerchants.Add(new() { MerchantId=merchant,EstabelecimentoId=store });
        options.AllowedCommands.AddRange(new[] { "confirm","start_preparation","ready_to_pickup","dispatch" });return options;
    }
    internal static IfoodCommandContext Context(string type="DELIVERY",string carrier="MERCHANT")
    {
        var merchant=Guid.NewGuid();var order=Guid.NewGuid();
        return new() { MerchantId=merchant,ExternalOrderId=order,EstabelecimentoId=Guid.NewGuid(),PedidoId=100,Revision=1,
            RemoteStatus="PLACED",PreparationState="awaiting_acceptance",SynchronizationState="synced",CancellationState="none",DataIsCurrent=true,
            Payload=IfoodOrderImportTests.Payload(merchant,order,type,carrier) };
    }
    [Theory]
    [InlineData("confirm","PLACED","awaiting_acceptance")]
    [InlineData("start_preparation","CONFIRMED","accepted")]
    [InlineData("ready_to_pickup","CONFIRMED","preparing")]
    [InlineData("dispatch","CONFIRMED","ready")]
    public void ValidCommandsRequireCurrentRevisionAndExplicitMerchantCapabilities(string command,string status,string prep)
    {
        var context=Context();context.RemoteStatus=status;context.PreparationState=prep;var options=Options(context.MerchantId,context.EstabelecimentoId);
        Assert.Equal(new Clock().Now,IfoodCommandRules.Validate(command,context,1,new Clock().Now,options).NotBeforeUtc);
        Assert.Equal("IFOOD_COMMAND_STALE_REVISION",Assert.Throws<DeliveryDomainException>(()=>IfoodCommandRules.Validate(command,context,2,new Clock().Now,options)).Code);
        options.AllowedCommands.Remove(command);
        Assert.Equal("IFOOD_COMMAND_NOT_ENABLED",Assert.Throws<DeliveryDomainException>(()=>IfoodCommandRules.Validate(command,context,1,new Clock().Now,options)).Code);
    }
    [Theory]
    [InlineData("TAKEOUT","MERCHANT")]
    [InlineData("DINE_IN","MERCHANT")]
    [InlineData("DELIVERY","IFOOD")]
    public void DispatchDoesNotApplyToTakeoutDineInOrIfoodCarrier(string type,string carrier)
    {
        var c=Context(type,carrier);c.RemoteStatus="CONFIRMED";c.PreparationState="ready";
        Assert.Equal("IFOOD_COMMAND_DISPATCH_NOT_APPLICABLE",Assert.Throws<DeliveryDomainException>(()=>IfoodCommandRules.Validate("dispatch",c,1,new Clock().Now,Options(c.MerchantId,c.EstabelecimentoId))).Code);
    }
    [Theory]
    [InlineData("terminal","IFOOD_ORDER_TERMINAL")]
    [InlineData("unknown_data","IFOOD_COMMAND_REQUIRES_REVIEW")]
    [InlineData("review","IFOOD_COMMAND_REQUIRES_REVIEW")]
    [InlineData("cancel_pending","IFOOD_CANCELLATION_PENDING")]
    [InlineData("not_ready","IFOOD_COMMAND_READY_EVENT_REQUIRED")]
    public void UnsafeStateNeverEnablesCommand(string issue,string expected)
    {
        var c=Context();c.RemoteStatus="CONFIRMED";c.PreparationState="ready";
        if(issue=="terminal")c.RemoteStatus="CANCELLED";
        if(issue=="unknown_data")c.DataIsCurrent=false;
        if(issue=="review")c.SynchronizationState="review";
        if(issue=="cancel_pending")c.CancellationState="requested";
        if(issue=="not_ready")c.PreparationState="preparing";
        Assert.Equal(expected,Assert.Throws<DeliveryDomainException>(()=>IfoodCommandRules.Validate("dispatch",c,1,new Clock().Now,Options(c.MerchantId,c.EstabelecimentoId))).Code);
    }
    [Fact]
    public void ConfirmationExpiresAtEightMinutesAndScheduledRuleIsExplicitWithoutInventedPrepTime()
    {
        var c=Context();var options=Options(c.MerchantId,c.EstabelecimentoId);var at=DateTimeOffset.Parse("2026-10-13T18:08:00Z");
        Assert.Equal("IFOOD_COMMAND_CONFIRMATION_DEADLINE",Assert.Throws<DeliveryDomainException>(()=>IfoodCommandRules.Validate("confirm",c,1,at,options)).Code);
        var root=JsonNode.Parse(c.Payload)!;root["orderTiming"]="SCHEDULED";
        root["scheduled"]=JsonNode.Parse("{\"preparationStartDateTime\":\"2026-10-13T19:00:00Z\",\"deliveryDateTimeStart\":\"2026-10-13T19:20:00Z\",\"deliveryDateTimeEnd\":\"2026-10-13T19:40:00Z\"}");c.Payload=root.ToJsonString();
        Assert.Throws<DeliveryDomainException>(()=>IfoodCommandRules.Validate("confirm",c,1,at,options));
        options.ScheduledConfirmationUsesPreparationStart=true;
        Assert.Equal(DateTimeOffset.Parse("2026-10-13T19:08:00Z"),IfoodCommandRules.Validate("confirm",c,1,at,options).DeadlineAtUtc);
        c.RemoteStatus="CONFIRMED";c.PreparationState="accepted";
        Assert.Equal(DateTimeOffset.Parse("2026-10-13T19:00:00Z"),IfoodCommandRules.Validate("start_preparation",c,1,at,options).NotBeforeUtc);
        root["scheduled"]!.AsObject().Remove("preparationStartDateTime");c.Payload=root.ToJsonString();
        Assert.Equal("IFOOD_COMMAND_SCHEDULE_INCOMPLETE",Assert.Throws<DeliveryDomainException>(()=>IfoodCommandRules.Validate("start_preparation",c,1,at,options)).Code);
    }
    [Fact]
    public void PreparationTimestampWithoutTimezoneCannotAuthorizeEarlyCooking()
    {
        var c=Context();var root=JsonNode.Parse(c.Payload)!;root["preparationStartDateTime"]="2026-10-13T18:10:00";c.Payload=root.ToJsonString();
        c.RemoteStatus="CONFIRMED";c.PreparationState="accepted";
        Assert.Equal("IFOOD_COMMAND_TIMESTAMP_AMBIGUOUS",Assert.Throws<DeliveryDomainException>(()=>IfoodCommandRules.Validate("start_preparation",c,1,new Clock().Now,Options(c.MerchantId,c.EstabelecimentoId))).Code);
    }
    [Fact]
    public void ConflictingScheduleAliasesRequireReview()
    {
        var c=Context();var root=JsonNode.Parse(c.Payload)!;root["orderTiming"]="SCHEDULED";
        root["schedule"]=JsonNode.Parse("{\"deliveryDateTimeStart\":\"2026-10-13T19:20:00Z\",\"deliveryDateTimeEnd\":\"2026-10-13T19:40:00Z\"}");
        root["scheduled"]=JsonNode.Parse("{\"deliveryDateTimeStart\":\"2026-10-13T20:20:00Z\",\"deliveryDateTimeEnd\":\"2026-10-13T20:40:00Z\"}");
        Assert.True(IfoodOrderNormalizer.Normalize(root.ToJsonString(),c.MerchantId,c.ExternalOrderId).RequiresReview);
        root["scheduled"]=root["schedule"]!.DeepClone();root["preparationStartDateTime"]="2026-10-13T19:00:00Z";
        root["scheduled"]!["preparationStartDateTime"]="2026-10-13T18:30:00Z";
        Assert.True(IfoodOrderNormalizer.Normalize(root.ToJsonString(),c.MerchantId,c.ExternalOrderId).RequiresReview);
    }
    internal static IfoodCommandClient Client(IfoodAuthenticationTests.Handler handler,IfoodCommandReservation item,out Mock<IIfoodTokenProvider> tokens)
    {
        var options=Options(item.MerchantId,item.EstabelecimentoId);var connections=new Mock<IIfoodConnectionRepository>();
        connections.Setup(c=>c.IsAuthorizedAsync(item.EstabelecimentoId,item.MerchantId,It.IsAny<CancellationToken>())).ReturnsAsync(true);
        tokens=new Mock<IIfoodTokenProvider>();tokens.Setup(t=>t.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new IfoodAccessToken("test-token",DateTimeOffset.UtcNow.AddHours(1),TimeSpan.FromHours(1)));
        var access=new IfoodMerchantAccess(connections.Object,tokens.Object,Microsoft.Extensions.Options.Options.Create(options));
        return new(new IfoodOrderImportTests.Factory(handler),access,tokens.Object,new Clock());
    }
    [Theory]
    [InlineData("confirm","confirm")]
    [InlineData("start_preparation","startPreparation")]
    [InlineData("ready_to_pickup","readyToPickup")]
    [InlineData("dispatch","dispatch")]
    public async Task ClientUsesOfficialFixedPostAfterDurableBeforeSendAndNeverMarks202Succeeded(string command,string path)
    {
        var item=new IfoodCommandReservation { Command=command,ExternalOrderId=Guid.NewGuid(),MerchantId=Guid.NewGuid(),EstabelecimentoId=Guid.NewGuid() };
        var persisted=false;var handler=new IfoodAuthenticationTests.Handler { Send=async(request,ct)=>
        {
            Assert.True(persisted);Assert.Equal(HttpMethod.Post,request.Method);Assert.Equal($"https://merchant-api.ifood.com.br/order/v1.0/orders/{item.ExternalOrderId:D}/{path}",request.RequestUri!.ToString());
            Assert.Equal("Bearer",request.Headers.Authorization!.Scheme);Assert.Equal(IfoodCommandRules.Payload(command),await request.Content!.ReadAsStringAsync(ct));
            Assert.False(request.Headers.Contains("Idempotency-Key"));
            return new(HttpStatusCode.Accepted) { Content=new StringContent("body-with-private-token") };
        } };
        var result=await Client(handler,item,out _).SendAsync(item,_=>{persisted=true;return Task.CompletedTask;},default);
        Assert.Equal("accepted",result.State);Assert.Equal(1,handler.Calls);Assert.DoesNotContain("private-token",JsonSerializer.Serialize(result));
    }
    [Theory]
    [InlineData(401,"retry")]
    [InlineData(429,"retry")]
    [InlineData(400,"rejected")]
    [InlineData(422,"rejected")]
    [InlineData(403,"rejected")]
    [InlineData(404,"review")]
    [InlineData(409,"review")]
    [InlineData(500,"uncertain")]
    [InlineData(503,"uncertain")]
    [InlineData(204,"uncertain")]
    public async Task ExplicitRejectionAndUnknownPostEffectAreDifferentWithoutAutomaticReplay(int status,string expected)
    {
        var item=new IfoodCommandReservation { Command="confirm",ExternalOrderId=Guid.NewGuid(),MerchantId=Guid.NewGuid(),EstabelecimentoId=Guid.NewGuid() };
        var handler=new IfoodAuthenticationTests.Handler { Send=(_,_)=>
        {
            var response=new HttpResponseMessage((HttpStatusCode)status) { Content=new StringContent("private-error-body") };
            response.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromDays(2));return Task.FromResult(response);
        } };
        var client=Client(handler,item,out var tokens);var result=await client.SendAsync(item,_=>Task.CompletedTask,default);
        Assert.Equal(expected,result.State);Assert.Equal(1,handler.Calls);
        if(status==401)tokens.Verify(t=>t.Invalidate(It.IsAny<IfoodAccessToken>()),Times.Once);
        if(status==429)Assert.Equal(TimeSpan.FromDays(2),result.RetryAfter);
        Assert.DoesNotContain("private-error-body",JsonSerializer.Serialize(result));
    }
    [Theory]
    [InlineData(true,"IFOOD_COMMAND_TIMEOUT")]
    [InlineData(false,"IFOOD_COMMAND_TRANSPORT")]
    public async Task TimeoutAndTransportFailureAfterCallbackRemainUncertain(bool timeout,string code)
    {
        var item=new IfoodCommandReservation { Command="confirm",ExternalOrderId=Guid.NewGuid(),MerchantId=Guid.NewGuid(),EstabelecimentoId=Guid.NewGuid() };
        var handler=new IfoodAuthenticationTests.Handler { Send=(_,_)=>throw (timeout?new TaskCanceledException("secret-body"):new HttpRequestException("secret-body")) };
        var result=await Client(handler,item,out _).SendAsync(item,_=>Task.CompletedTask,default);
        Assert.Equal("uncertain",result.State);Assert.Equal(code,result.Code);Assert.DoesNotContain("secret-body",result.ToString());
    }
    [Fact]
    public async Task FailedPreSendFenceMakesNoPostAndDisabledServiceDoesNotOpenDatabase()
    {
        var item=new IfoodCommandReservation { Command="confirm",ExternalOrderId=Guid.NewGuid(),MerchantId=Guid.NewGuid(),EstabelecimentoId=Guid.NewGuid() };
        var handler=new IfoodAuthenticationTests.Handler();
        await Assert.ThrowsAsync<IfoodIntegrationException>(()=>Client(handler,item,out _).SendAsync(item,_=>throw new IfoodIntegrationException("IFOOD_COMMAND_LEASE_LOST"),default));
        Assert.Equal(0,handler.Calls);
        await using var source=NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused");
        var options=Microsoft.Extensions.Options.Options.Create(new IfoodOptions());
        var service=new IfoodCommandService(new(source,options,new Clock()),new Mock<IIfoodCommandClient>().Object,options);
        Assert.Equal(0,await service.RunOnceAsync());await Assert.ThrowsAsync<DeliveryDomainException>(()=>service.SubmitAsync(Guid.NewGuid(),1,100,new()));
    }
    [Fact]
    public void InvalidCommandConfigurationAndEndpointPermissionAreExplicit()
    {
        var options=IfoodAuthenticationTests.EnabledOptions();options.CommandsEnabled=true;
        Assert.True(new IfoodOptionsValidator().Validate(null,options).Failed);
        var submit=typeof(IfoodCommandsController).GetMethod("Submit")!;
        Assert.NotEmpty(submit.GetCustomAttributes(typeof(APIBack.Attributes.RequirePermissionAttribute),true));
        Assert.DoesNotContain("Payload",JsonSerializer.Serialize(new IfoodCommandDto()));
    }
}
