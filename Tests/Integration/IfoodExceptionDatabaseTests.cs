using System.Net;
using System.Text.Json;
using APIBack.DTOs.Delivery;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Commands;
using APIBack.Integrations.Ifood.Events;
using APIBack.Integrations.Ifood.Exceptions;
using APIBack.Repository;
using APIBack.Service;
using APIBack.Tests.Unit;
using Moq;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

public sealed partial class IfoodIntegrationSchemaDatabaseTests
{
    private const string ExceptionFile="20261013_08_ifood_cancellation_disputes.sql";
    private const string DefaultReasons="[{\"code\":\"00501\",\"description\":\"Cliente desistiu\",\"secret\":\"not-copied\"}]";
    private static async Task<Database> ExceptionDatabase(bool import=true)
    {
        var db=await CommandDatabase(import);
        try { await db.Migrate(ExceptionFile);return db; }
        catch { await db.DisposeAsync();throw; }
    }
    private sealed class ExceptionRig
    {
        public IfoodOptions Options=null!;
        public IfoodCommandTests.Clock Clock=null!;
        public IfoodCommandRepository Repo=null!;
        public IfoodExceptionService Service=null!;
        public IfoodAuthenticationTests.Handler Handler=null!;
        public readonly List<(string Method,string Url,string Body)> Calls=new();
        public string ReasonsJson=DefaultReasons;
        public HttpStatusCode PostStatus=HttpStatusCode.Accepted;
        public Func<Task>? TokenAction;
        public IEnumerable<(string Method,string Url,string Body)> Posts { get { lock(Calls) return Calls.Where(c=>c.Method=="POST").ToArray(); } }
        public int Gets { get { lock(Calls) return Calls.Count(c=>c.Method=="GET"); } }
    }
    private static ExceptionRig Rig(Database db)
    {
        var rig=new ExceptionRig { Clock=new(),Options=IfoodExceptionTests.Options(db.MerchantA,db.StoreA) };
        rig.Handler=new IfoodAuthenticationTests.Handler { Send=async(request,_)=>
        {
            var body=request.Content==null?"":await request.Content.ReadAsStringAsync();
            lock(rig.Calls) rig.Calls.Add((request.Method.Method,request.RequestUri!.ToString(),body));
            if(request.Method==HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(rig.ReasonsJson) };
            return new HttpResponseMessage(rig.PostStatus);
        } };
        var tokens=new Mock<IIfoodTokenProvider>();
        tokens.Setup(t=>t.GetAsync(It.IsAny<CancellationToken>())).Returns(async()=>
        {
            if(rig.TokenAction!=null) await rig.TokenAction();
            return new IfoodAccessToken("test-only-token",DateTimeOffset.UtcNow.AddHours(1),TimeSpan.FromHours(1));
        });
        var bound=IfoodAuthenticationTests.Monitor(rig.Options);var factory=new IfoodOrderImportTests.Factory(rig.Handler);
        var access=new IfoodMerchantAccess(db.Connections,tokens.Object,bound);
        rig.Repo=new(db.Source,bound,rig.Clock);
        rig.Service=new(rig.Repo,new IfoodCommandClient(factory,access,tokens.Object,rig.Clock),new IfoodCancellationReasonsClient(factory,access,tokens.Object),bound,rig.Clock);
        return rig;
    }
    private static Task<long> Revision(Database db)=>db.Scalar<long>("SELECT revision FROM ifood_order_snapshots");
    private static async Task<IfoodCancellationRequest> CancelRequest(Database db,ExceptionRig rig,string reason="00501")
    {
        var catalog=await rig.Service.ReasonsAsync(db.StoreA,100);
        return new() { OperationId=Guid.NewGuid(),ExpectedRevision=catalog.Revision,CatalogId=catalog.CatalogId,Reason=reason };
    }
    private static Task<IfoodInboxReservation> DisputeEvent(Database db,Guid dispute,int minute,string expires="2026-10-13T18:30:00Z")
        => ProjectionEvent(db,"HSD",minute,new
        {
            disputeId=dispute,handshakeType="AFTER_DELIVERY",action="CANCELLATION",expiresAt=expires,timeoutAction="REJECT_CANCELLATION",
            alternatives=new object[] { new { id=IfoodExceptionTests.RefundId,type="REFUND",metadata=new { maxAmount=new { value="05000",currency="BRL" } } } }
        });
    private static async Task<IfoodDisputeResponseRequest> DisputeRequest(Database db,ExceptionRig rig,Guid dispute,string action)
    {
        var options=await rig.Service.OptionsAsync(db.StoreA,100,dispute);
        return new() { OperationId=Guid.NewGuid(),ExpectedRevision=options.OrderRevision,ExpectedDisputeRevision=options.Revision,Action=action };
    }
    private static async Task<(Guid Dispute,Guid Operation)> SendDispute(Database db,ExceptionRig rig,string action,int minute,Action<IfoodDisputeResponseRequest>? configure=null)
    {
        var dispute=Guid.NewGuid();Assert.Equal("applied",await Project(db,await DisputeEvent(db,dispute,minute)));
        var request=await DisputeRequest(db,rig,dispute,action);configure?.Invoke(request);
        await rig.Service.RespondAsync(db.StoreA,1,100,dispute,request);Assert.Equal(1,await rig.Service.RunOnceAsync());
        return (dispute,request.OperationId);
    }
    private static async Task<string> Code(Func<Task> action)
        => (await Assert.ThrowsAsync<DeliveryDomainException>(action)).Code;

    [DeliveryDatabaseFact]
    public async Task CancellationUsesFreshDynamicReasonsAndSendsOnceThenOnlyEventsResolveItAndFinalCancelUpdatesPedido()
    {
        await using var db=await ExceptionDatabase();var rig=Rig(db);
        var catalog=await rig.Service.ReasonsAsync(db.StoreA,100);
        Assert.Equal("00501",Assert.Single(catalog.Reasons).Code);
        Assert.DoesNotContain("not-copied",await db.Scalar<string>("SELECT reasons::text FROM ifood_cancellation_reason_snapshots"));
        var request=new IfoodCancellationRequest { OperationId=Guid.NewGuid(),ExpectedRevision=catalog.Revision,CatalogId=catalog.CatalogId,Reason="00501" };
        var unknown=new IfoodCancellationRequest { OperationId=Guid.NewGuid(),ExpectedRevision=catalog.Revision,CatalogId=catalog.CatalogId,Reason="99999" };
        Assert.Equal("IFOOD_CANCELLATION_REASON_NOT_IN_CURRENT_CATALOG",await Code(()=>rig.Service.CancelAsync(db.StoreA,1,100,unknown)));
        var otherCatalog=new IfoodCancellationRequest { OperationId=Guid.NewGuid(),ExpectedRevision=catalog.Revision,CatalogId=Guid.NewGuid(),Reason="00501" };
        Assert.Equal("IFOOD_CANCELLATION_REASON_NOT_IN_CURRENT_CATALOG",await Code(()=>rig.Service.CancelAsync(db.StoreA,1,100,otherCatalog)));
        Assert.Equal("queued",(await rig.Service.CancelAsync(db.StoreA,1,100,request)).State);
        await rig.Service.CancelAsync(db.StoreA,1,100,request);
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_outbound_commands"));
        Assert.Equal("IFOOD_COMMAND_OPERATION_CONFLICT",await Code(()=>rig.Service.CancelAsync(db.StoreA,2,100,request)));
        var second=new IfoodCancellationRequest { OperationId=Guid.NewGuid(),ExpectedRevision=catalog.Revision,CatalogId=catalog.CatalogId,Reason="00501" };
        Assert.Equal("IFOOD_COMMAND_ACTIVE_OPERATION",await Code(()=>rig.Service.CancelAsync(db.StoreA,1,100,second)));
        var queued=await new PedidoConsultaRepository(db.Source).GetAsync(db.StoreA,100);
        Assert.Equal("request_cancellation",queued!.IdentidadeExterna!.ActiveCommand!.Command);

        Assert.Equal(1,await rig.Service.RunOnceAsync());
        // Uma consulta de motivos ao criar o catálogo e outra imediatamente antes do POST.
        Assert.Equal(2,rig.Gets);
        var post=Assert.Single(rig.Posts);
        Assert.EndsWith($"/order/v1.0/orders/{db.OrderA:D}/requestCancellation",post.Url);Assert.Equal("00501",JsonDocument.Parse(post.Body).RootElement.GetProperty("reason").GetString());
        var sent=(await rig.Repo.ReadAsync(db.StoreA,request.OperationId))!;
        Assert.Equal("uncertain",sent.State);Assert.NotNull(sent.HttpAcceptedAtUtc);Assert.NotNull(sent.SendStartedAtUtc);
        Assert.Equal("PLACED",await db.Scalar<string>("SELECT remote_status FROM ifood_order_snapshots"));
        Assert.Equal(6,await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id>=100"));
        Assert.Equal(0,await rig.Service.RunOnceAsync());

        Assert.Equal("applied",await Project(db,await ProjectionEvent(db,"CAR",2)));
        Assert.True(await db.Scalar<bool>("SELECT cancellation_requested_event_id IS NOT NULL FROM ifood_outbound_commands"));
        Assert.Equal("uncertain",(await rig.Repo.ReadAsync(db.StoreA,request.OperationId))!.State);
        Assert.Equal(6,await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id>=100"));
        Assert.Equal("IFOOD_CANCELLATION_PENDING",await Code(()=>rig.Service.ReasonsAsync(db.StoreA,100)));

        var cancelled=await ProjectionEvent(db,"CAN",3);Assert.Equal("applied",await Project(db,cancelled));
        var done=(await rig.Repo.ReadAsync(db.StoreA,request.OperationId))!;
        Assert.Equal("succeeded",done.State);Assert.Equal(cancelled.EventId,done.ConfirmedEventId);
        Assert.Equal(4,await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id>=100"));
        Assert.True(await db.Scalar<bool>("SELECT motoboy_responsavel IS NULL FROM pedido WHERE id>=100"));
        Assert.Equal("IFOOD_ORDER_TERMINAL",await Code(()=>rig.Service.ReasonsAsync(db.StoreA,100)));
        Assert.Single(rig.Posts);
    }
    [DeliveryDatabaseFact]
    public async Task CancellationRejectionNeedsCorrelatedRequestEventAndRetryOnlyChainsFromRejectedParent()
    {
        await using var db=await ExceptionDatabase();var rig=Rig(db);
        var first=await CancelRequest(db,rig);await rig.Service.CancelAsync(db.StoreA,1,100,first);Assert.Equal(1,await rig.Service.RunOnceAsync());
        var requested=await ProjectionEvent(db,"CAR",2);await Project(db,requested);
        // Falha anterior ao evento correlacionado não prova rejeição da nossa intenção.
        await Project(db,await ProjectionEvent(db,"CARF",3,new { CANCELLATION_REQUESTED_EVENT_ID=requested.EventId }));
        var rejected=(await rig.Repo.ReadAsync(db.StoreA,first.OperationId))!;
        Assert.Equal("rejected",rejected.State);Assert.Equal("IFOOD_CANCELLATION_REQUEST_FAILED",rejected.LastErrorCode);
        Assert.Equal("rejected",await db.Scalar<string>("SELECT cancellation_state FROM ifood_order_snapshots"));

        var retry=await CancelRequest(db,rig);retry.ParentOperationId=Guid.NewGuid();
        Assert.Equal("IFOOD_EXCEPTION_PARENT_NOT_REJECTED",await Code(()=>rig.Service.CancelAsync(db.StoreA,1,100,retry)));
        retry.ParentOperationId=first.OperationId;Assert.Equal("queued",(await rig.Service.CancelAsync(db.StoreA,1,100,retry)).State);
        Assert.Equal(1,await rig.Service.RunOnceAsync());Assert.Equal(2,rig.Posts.Count());

        await Project(db,await ProjectionEvent(db,"CAR",4));
        await Project(db,await ProjectionEvent(db,"CARF",5,new { CANCELLATION_REQUESTED_EVENT_ID=Guid.NewGuid() }));
        var review=(await rig.Repo.ReadAsync(db.StoreA,retry.OperationId))!;
        Assert.Equal("review",review.State);Assert.Equal("IFOOD_CANCELLATION_REJECTION_UNCORRELATED",review.LastErrorCode);
        Assert.Equal(0,await rig.Service.RunOnceAsync());Assert.Equal(2,rig.Posts.Count());
    }
    [DeliveryDatabaseFact]
    public async Task CancellationCatalogExpiryStaleOrderAndRemovedReasonBlockBeforeAnyPost()
    {
        await using var db=await ExceptionDatabase();var rig=Rig(db);
        var expired=await CancelRequest(db,rig);rig.Clock.Now=rig.Clock.Now.AddSeconds(rig.Options.CancellationReasonsTtlSeconds+1);
        Assert.Equal("IFOOD_CANCELLATION_REASON_NOT_IN_CURRENT_CATALOG",await Code(()=>rig.Service.CancelAsync(db.StoreA,1,100,expired)));
        var stale=await CancelRequest(db,rig);await db.Execute("UPDATE ifood_order_snapshots SET revision=revision+1");
        Assert.Equal("IFOOD_COMMAND_STALE_REVISION",await Code(()=>rig.Service.CancelAsync(db.StoreA,1,100,stale)));
        var request=await CancelRequest(db,rig);await rig.Service.CancelAsync(db.StoreA,1,100,request);
        rig.ReasonsJson="[{\"code\":\"00999\",\"description\":\"Outro motivo\"}]";
        Assert.Equal(0,await rig.Service.RunOnceAsync());
        var result=(await rig.Repo.ReadAsync(db.StoreA,request.OperationId))!;
        Assert.Equal("rejected",result.State);Assert.Equal("IFOOD_CANCELLATION_REASON_NO_LONGER_AVAILABLE",result.LastErrorCode);Assert.Empty(rig.Posts);
        Assert.Null(result.SendStartedAtUtc);
    }
    [DeliveryDatabaseFact]
    public async Task DisputeAlternativeIsRevisionedLockedAgainstSecondAnswerAndOnlyExactSettlementResolvesWithoutLocalRefund()
    {
        await using var db=await ExceptionDatabase();var rig=Rig(db);var dispute=Guid.NewGuid();
        var pendingBefore=await db.Scalar<decimal>("SELECT pending_amount FROM ifood_order_snapshots");
        Assert.Equal("applied",await Project(db,await DisputeEvent(db,dispute,1)));
        var options=await rig.Service.OptionsAsync(db.StoreA,100,dispute);
        Assert.True(options.CanRespond);Assert.Equal(new[] { "accept","reject","alternative" },options.Actions);
        var refund=Assert.Single(options.Alternatives);Assert.Equal("05000",refund.MaxAmountMinor);Assert.Equal("BRL",refund.Currency);
        var request=new IfoodDisputeResponseRequest { OperationId=Guid.NewGuid(),ExpectedRevision=options.OrderRevision,ExpectedDisputeRevision=options.Revision,
            Action="alternative",AlternativeId=refund.Id,AmountMinor="0200",Currency="BRL" };
        var tooMuch=new IfoodDisputeResponseRequest { OperationId=Guid.NewGuid(),ExpectedRevision=options.OrderRevision,ExpectedDisputeRevision=options.Revision,
            Action="alternative",AlternativeId=refund.Id,AmountMinor="05001",Currency="BRL" };
        Assert.Equal("IFOOD_DISPUTE_AMOUNT_NOT_ALLOWED",await Code(()=>rig.Service.RespondAsync(db.StoreA,1,100,dispute,tooMuch)));
        var stale=new IfoodDisputeResponseRequest { OperationId=Guid.NewGuid(),ExpectedRevision=options.OrderRevision,ExpectedDisputeRevision=options.Revision+1,Action="accept" };
        Assert.Equal("IFOOD_DISPUTE_STALE_REVISION",await Code(()=>rig.Service.RespondAsync(db.StoreA,1,100,dispute,stale)));
        Assert.Equal("queued",(await rig.Service.RespondAsync(db.StoreA,1,100,dispute,request)).State);
        await rig.Service.RespondAsync(db.StoreA,1,100,dispute,request);
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_outbound_commands"));
        var other=new IfoodDisputeResponseRequest { OperationId=Guid.NewGuid(),ExpectedRevision=options.OrderRevision,ExpectedDisputeRevision=options.Revision,Action="accept" };
        Assert.Equal("IFOOD_DISPUTE_RESPONSE_ALREADY_PENDING",await Code(()=>rig.Service.RespondAsync(db.StoreA,1,100,dispute,other)));
        var locked=await rig.Service.OptionsAsync(db.StoreA,100,dispute);Assert.False(locked.CanRespond);Assert.Equal(request.OperationId,locked.ResponseOperationId);
        var detail=await new PedidoConsultaRepository(db.Source).GetAsync(db.StoreA,100);
        Assert.Equal(request.OperationId,Assert.Single(detail!.DisputasIfood).ResponseOperationId);

        Assert.Equal(1,await rig.Service.RunOnceAsync());
        var post=Assert.Single(rig.Posts);Assert.Equal($"https://merchant-api.ifood.com.br/order/v1.0/disputes/{dispute:D}/alternatives/{refund.Id:D}",post.Url);
        using(var body=JsonDocument.Parse(post.Body))
        {
            var amount=body.RootElement.GetProperty("metadata").GetProperty("amount");
            Assert.Equal("REFUND",body.RootElement.GetProperty("type").GetString());Assert.Equal("0200",amount.GetProperty("value").GetString());Assert.Equal("BRL",amount.GetProperty("currency").GetString());
        }
        Assert.Equal("uncertain",(await rig.Repo.ReadAsync(db.StoreA,request.OperationId))!.State);
        Assert.Equal(0,await rig.Service.RunOnceAsync());Assert.Single(rig.Posts);

        // Resolução com outro valor não comprova a resposta enviada.
        var wrong=await ProjectionEvent(db,"HSS",3,new { disputeId=dispute,status="ALTERNATIVE_REPLIED",
            selectedDisputeAlternative=new { id=refund.Id,type="REFUND",metadata=new { amount=new { value="0300",currency="BRL" } } } });
        await Project(db,wrong);
        var mismatch=(await rig.Repo.ReadAsync(db.StoreA,request.OperationId))!;
        Assert.Equal("review",mismatch.State);Assert.Equal("IFOOD_DISPUTE_SETTLEMENT_REQUIRES_REVIEW",mismatch.LastErrorCode);
        Assert.Equal("responded",await db.Scalar<string>("SELECT state FROM ifood_disputes"));
        Assert.Equal(pendingBefore,await db.Scalar<decimal>("SELECT pending_amount FROM ifood_order_snapshots"));
        Assert.Equal(6,await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id>=100"));
        Assert.Equal("PLACED",await db.Scalar<string>("SELECT remote_status FROM ifood_order_snapshots"));
    }
    [DeliveryDatabaseFact]
    public async Task DisputeSettlementResolvesOnlyTheMatchingResponseAndRecordsDifferentOrExpiredOutcomes()
    {
        await using var db=await ExceptionDatabase();var rig=Rig(db);var minute=1;
        async Task<(Guid Dispute,Guid Operation)> Send(string action,Action<IfoodDisputeResponseRequest>? configure=null)
        { var result=await SendDispute(db,rig,action,minute,configure);minute+=2;return result; }
        async Task<IfoodCommandDto> Settle(Guid dispute,Guid operation,object metadata)
        { await Project(db,await ProjectionEvent(db,"HSS",minute++,metadata));return (await rig.Repo.ReadAsync(db.StoreA,operation))!; }

        var accept=await Send("accept");
        Assert.EndsWith($"/disputes/{accept.Dispute:D}/accept",rig.Posts.Last().Url);Assert.Empty(JsonDocument.Parse(rig.Posts.Last().Body).RootElement.EnumerateObject());
        var accepted=await Settle(accept.Dispute,accept.Operation,new { disputeId=accept.Dispute,status="ACCEPTED" });
        Assert.Equal("succeeded",accepted.State);Assert.NotNull(accepted.ConfirmedEventId);
        Assert.Equal("resolved",await db.Scalar<string>($"SELECT state FROM ifood_disputes WHERE dispute_id='{accept.Dispute}'"));

        var reject=await Send("reject",r=>r.Reason="Itens conferidos na saída");
        Assert.EndsWith($"/disputes/{reject.Dispute:D}/reject",rig.Posts.Last().Url);Assert.Equal("Itens conferidos na saída",JsonDocument.Parse(rig.Posts.Last().Body).RootElement.GetProperty("reason").GetString());
        var different=await Settle(reject.Dispute,reject.Operation,new { disputeId=reject.Dispute,status="ACCEPTED" });
        Assert.Equal("rejected",different.State);Assert.Equal("IFOOD_DISPUTE_RESOLVED_WITH_DIFFERENT_RESPONSE",different.LastErrorCode);

        var expired=await Send("reject",r=>r.Reason="Itens conferidos");
        var timeout=await Settle(expired.Dispute,expired.Operation,new { disputeId=expired.Dispute,status="EXPIRED" });
        Assert.Equal("rejected",timeout.State);Assert.Equal("IFOOD_DISPUTE_EXPIRED_REMOTELY",timeout.LastErrorCode);
        Assert.Equal("expired",await db.Scalar<string>($"SELECT state FROM ifood_disputes WHERE dispute_id='{expired.Dispute}'"));
        // Disputa encerrada libera nenhuma nova resposta e a operação rejeitada não deixa a trava ativa.
        Assert.False((await rig.Service.OptionsAsync(db.StoreA,100,expired.Dispute)).CanRespond);

        var alternative=await Send("alternative",r=>{ r.AlternativeId=IfoodExceptionTests.RefundId;r.AmountMinor="0200";r.Currency="BRL"; });
        var matched=await Settle(alternative.Dispute,alternative.Operation,new { disputeId=alternative.Dispute,status="ALTERNATIVE_REPLIED",
            selectedDisputeAlternative=new { id=IfoodExceptionTests.RefundId,type="REFUND",metadata=new { amount=new { value="200",currency="BRL" } } } });
        Assert.Equal("succeeded",matched.State);
        Assert.Equal("PLACED",await db.Scalar<string>("SELECT remote_status FROM ifood_order_snapshots"));
        Assert.Equal(4,rig.Posts.Count());
    }
    [DeliveryDatabaseFact]
    public async Task DisputeExpiredOrRevisedAfterQueueingIsRejectedBeforePostAndReleasesLockForNewRevisionedAnswer()
    {
        await using var db=await ExceptionDatabase();var rig=Rig(db);
        var late=Guid.NewGuid();await Project(db,await DisputeEvent(db,late,1,"2026-10-13T18:10:00Z"));
        var lateRequest=await DisputeRequest(db,rig,late,"accept");await rig.Service.RespondAsync(db.StoreA,1,100,late,lateRequest);
        rig.Clock.Now=DateTimeOffset.Parse("2026-10-13T18:10:00Z");
        Assert.Equal(0,await rig.Service.RunOnceAsync());
        var expired=(await rig.Repo.ReadAsync(db.StoreA,lateRequest.OperationId))!;
        Assert.Equal("rejected",expired.State);Assert.Empty(rig.Posts);Assert.Null(expired.SendStartedAtUtc);
        Assert.Null(await db.Scalar<Guid?>($"SELECT response_operation_id FROM ifood_disputes WHERE dispute_id='{late}'"));
        Assert.False((await rig.Service.OptionsAsync(db.StoreA,100,late)).CanRespond);

        rig.Clock.Now=DateTimeOffset.Parse("2026-10-13T18:01:00Z");var revised=Guid.NewGuid();await Project(db,await DisputeEvent(db,revised,2));
        var first=await DisputeRequest(db,rig,revised,"accept");await rig.Service.RespondAsync(db.StoreA,1,100,revised,first);
        rig.TokenAction=()=>db.Execute($"UPDATE ifood_disputes SET revision=revision+1 WHERE dispute_id='{revised}'");
        Assert.Equal(0,await rig.Service.RunOnceAsync());rig.TokenAction=null;
        var stale=(await rig.Repo.ReadAsync(db.StoreA,first.OperationId))!;
        Assert.Equal("rejected",stale.State);Assert.Equal("IFOOD_DISPUTE_STALE_REVISION",stale.LastErrorCode);Assert.Empty(rig.Posts);
        var next=await DisputeRequest(db,rig,revised,"accept");next.ParentOperationId=Guid.NewGuid();
        Assert.Equal("IFOOD_EXCEPTION_PARENT_NOT_REJECTED",await Code(()=>rig.Service.RespondAsync(db.StoreA,1,100,revised,next)));
        next.ParentOperationId=first.OperationId;await rig.Service.RespondAsync(db.StoreA,1,100,revised,next);
        Assert.Equal(1,await rig.Service.RunOnceAsync());Assert.Single(rig.Posts);
    }
    [DeliveryDatabaseFact]
    public async Task ExceptionTenantAuthorizationDisabledActionAndOrderSupersessionAreEnforcedWithoutHttp()
    {
        await using var db=await ExceptionDatabase();var rig=Rig(db);var dispute=Guid.NewGuid();await Project(db,await DisputeEvent(db,dispute,1));
        Assert.Equal("IFOOD_ORDER_NOT_FOUND",await Code(()=>rig.Service.ReasonsAsync(db.StoreB,100)));
        Assert.Equal("IFOOD_ORDER_NOT_FOUND",await Code(async()=>await rig.Service.OptionsAsync(db.StoreB,100,dispute)));
        Assert.Equal("IFOOD_DISPUTE_NOT_FOUND",await Code(async()=>await rig.Service.OptionsAsync(db.StoreA,100,Guid.NewGuid())));
        var request=await DisputeRequest(db,rig,dispute,"reject");request.Reason="Itens conferidos";
        Assert.Equal("IFOOD_ORDER_NOT_FOUND",await Code(()=>rig.Service.RespondAsync(db.StoreB,1,100,dispute,request)));

        rig.Options.AllowedExceptionActions.Remove("dispute_reject");
        Assert.DoesNotContain("reject",(await rig.Service.OptionsAsync(db.StoreA,100,dispute)).Actions);
        Assert.Equal("IFOOD_EXCEPTION_ACTION_NOT_ENABLED",await Code(()=>rig.Service.RespondAsync(db.StoreA,1,100,dispute,request)));
        rig.Options.AllowedExceptionActions.Add("dispute_reject");

        var cancel=await CancelRequest(db,rig);await rig.Service.CancelAsync(db.StoreA,1,100,cancel);
        var before=rig.Gets;
        await db.Execute("UPDATE ifood_connections SET status='revoked' WHERE merchant_id=@Merchant",new { Merchant=db.MerchantA });
        Assert.Equal(0,await rig.Service.RunOnceAsync());Assert.Equal("queued",(await rig.Repo.ReadAsync(db.StoreA,cancel.OperationId))!.State);
        Assert.Equal(before,rig.Gets);Assert.Empty(rig.Posts);
        await Assert.ThrowsAsync<IfoodIntegrationException>(()=>rig.Service.ReasonsAsync(db.StoreA,100));
        await db.Execute("UPDATE ifood_connections SET status='authorized' WHERE merchant_id=@Merchant",new { Merchant=db.MerchantA });

        // Cancelamento final anterior ao envio encerra a intenção sem POST.
        await Project(db,await ProjectionEvent(db,"CAN",2));
        var superseded=(await rig.Repo.ReadAsync(db.StoreA,cancel.OperationId))!;
        Assert.Equal("rejected",superseded.State);Assert.Equal("IFOOD_COMMAND_SUPERSEDED_BY_CANCELLATION",superseded.LastErrorCode);
        Assert.Equal(0,await rig.Service.RunOnceAsync());Assert.Empty(rig.Posts);

        rig.Options.ExceptionMerchants.Clear();
        Assert.Equal("IFOOD_EXCEPTION_MERCHANT_NOT_ENABLED",await Code(()=>rig.Service.ReasonsAsync(db.StoreA,100)));
    }
    [DeliveryDatabaseFact]
    public async Task ExceptionUncertainRemoteResultsAndCrashAfterMarkerAreNeverReplayedOrBypassedByNewOperation()
    {
        await using var db=await ExceptionDatabase();var rig=Rig(db);rig.PostStatus=HttpStatusCode.ServiceUnavailable;
        var cancel=await CancelRequest(db,rig);await rig.Service.CancelAsync(db.StoreA,1,100,cancel);Assert.Equal(1,await rig.Service.RunOnceAsync());
        Assert.Equal("uncertain",(await rig.Repo.ReadAsync(db.StoreA,cancel.OperationId))!.State);
        Assert.Equal(0,await rig.Service.RunOnceAsync());Assert.Single(rig.Posts);
        Assert.Equal("IFOOD_COMMAND_ACTIVE_OPERATION",await Code(async()=>await rig.Service.CancelAsync(db.StoreA,1,100,await CancelRequest(db,rig))));

        await using var other=await ExceptionDatabase();var rigB=Rig(other);rigB.PostStatus=HttpStatusCode.UnprocessableEntity;
        var dispute=Guid.NewGuid();await Project(other,await DisputeEvent(other,dispute,1));
        var request=await DisputeRequest(other,rigB,dispute,"accept");await rigB.Service.RespondAsync(other.StoreA,1,100,dispute,request);Assert.Equal(1,await rigB.Service.RunOnceAsync());
        var review=(await rigB.Repo.ReadAsync(other.StoreA,request.OperationId))!;
        Assert.Equal("review",review.State);Assert.Equal("IFOOD_DISPUTE_ALREADY_ANSWERED_OR_STATE_UNKNOWN",review.LastErrorCode);
        Assert.False((await rigB.Service.OptionsAsync(other.StoreA,100,dispute)).CanRespond);
        Assert.Equal(0,await rigB.Service.RunOnceAsync());Assert.Single(rigB.Posts);

        await using var crash=await ExceptionDatabase();var rigC=Rig(crash);
        var crashed=await CancelRequest(crash,rigC);await rigC.Service.CancelAsync(crash.StoreA,1,100,crashed);
        var item=(await rigC.Repo.ClaimAsync(default,new[] { "request_cancellation" },new[] { crash.MerchantA }))!;
        var reasons=new[] { new IfoodCancellationReasonDto("00501","Cliente desistiu") };
        await rigC.Repo.MarkExceptionSendingAsync(item,reasons,rigC.Clock.Now);
        await crash.Execute("UPDATE ifood_outbound_commands SET lease_until_utc=clock_timestamp()-INTERVAL '1 second'");
        Assert.Null(await rigC.Repo.ClaimAsync(default,new[] { "request_cancellation" },new[] { crash.MerchantA }));
        Assert.Equal("uncertain",(await rigC.Repo.ReadAsync(crash.StoreA,crashed.OperationId))!.State);Assert.Empty(rigC.Posts);
    }
    [DeliveryDatabaseFact]
    public async Task DisputeSettlementProjectionAndOperationResolutionRollbackTogetherAndRecoverOnRetry()
    {
        await using var db=await ExceptionDatabase();var rig=Rig(db);
        var (dispute,operation)=await SendDispute(db,rig,"accept",1);
        var settlement=await ProjectionEvent(db,"HSS",3,new { disputeId=dispute,status="ACCEPTED" });
        await db.Execute("ALTER TABLE ifood_outbound_commands ADD CONSTRAINT if10_resolution_failure CHECK(state<>'succeeded')");
        await Assert.ThrowsAnyAsync<Exception>(()=>Project(db,settlement));
        Assert.Equal("uncertain",(await rig.Repo.ReadAsync(db.StoreA,operation))!.State);
        Assert.Equal("open",await db.Scalar<string>($"SELECT state FROM ifood_disputes WHERE dispute_id='{dispute}'"));
        Assert.Equal(0,await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_dispute_settlements"));
        await db.Execute("ALTER TABLE ifood_outbound_commands DROP CONSTRAINT if10_resolution_failure");
        await Project(db,settlement);
        Assert.Equal("succeeded",(await rig.Repo.ReadAsync(db.StoreA,operation))!.State);
        Assert.Equal("resolved",await db.Scalar<string>($"SELECT state FROM ifood_disputes WHERE dispute_id='{dispute}'"));
    }
    [DeliveryDatabaseFact]
    public async Task ExceptionMigrationIsRepeatableConstrainsDisputeIdentityAndRollbackPreservesOperationsInDependencyOrder()
    {
        await using var db=await ExceptionDatabase(false);await db.Migrate(ExceptionFile);
        async Task<string> Down(string file)=>await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Migrations","Delivery","rollback",file));
        await Assert.ThrowsAsync<PostgresException>(async()=>await db.Execute(await Down("20261013_07_ifood_outbound_commands.down.sql")));
        await db.Execute(await Down("20261013_08_ifood_cancellation_disputes.down.sql"));await db.Migrate(ExceptionFile);
        await db.Link(db.MerchantA,db.StoreA,1,db.OrderA);
        const string insert="""
INSERT INTO ifood_outbound_commands(operation_id,merchant_id,external_order_id,estabelecimento_id,command_type,payload,payload_hash,expected_order_revision,target_dispute_id)
VALUES(@Operation,@Merchant,@Order,@Store,@Type,'{}',@Hash,1,@Dispute)
""";
        object Args(string type,Guid? dispute=null)=>new { Operation=Guid.NewGuid(),Merchant=db.MerchantA,Order=db.OrderA,Store=db.StoreA,Type=type,Hash=new string('a',64),Dispute=dispute };
        await Reject(PostgresErrorCodes.ForeignKeyViolation,()=>db.Execute(insert,Args("dispute_accept",Guid.NewGuid())));
        await db.Execute(insert,Args("request_cancellation"));
        await Reject(PostgresErrorCodes.UniqueViolation,()=>db.Execute(insert,Args("confirm")));
        await Assert.ThrowsAsync<PostgresException>(async()=>await db.Execute(await Down("20261013_08_ifood_cancellation_disputes.down.sql")));
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_outbound_commands"));
    }
}
