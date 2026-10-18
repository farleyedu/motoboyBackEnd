using APIBack.Controllers;
using APIBack.DTOs.Common;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Config;
using APIBack.Integrations.Ifood.Reconciliation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class IfoodDiagnosticsControllerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private sealed class FakeReconciliation : IIfoodReconciliationRepository
    {
        public IfoodPollingHealth? Health { get; set; }
        public List<IfoodAlertRow> Alerts { get; } = new();
        public string? ThrowCode { get; set; }
        public (Guid Store, int Pedido, int User, string? Note)? Last { get; private set; }
        public Task<IReadOnlyList<IfoodOrderEventRow>> ListOrderEventsAsync(Guid store, int pedido, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IfoodOrderEventRow>>(new[] { new IfoodOrderEventRow { InboxId = 9, EventCode = "DDCR", State = "review", ReasonCode = "IFOOD_EVENT_CODE_CONFLICT" } });
        private Task Do(Guid store, int pedido, int user, string? note)
        { if (ThrowCode != null) throw new IfoodIntegrationException(ThrowCode); Last = (store, pedido, user, note); return Task.CompletedTask; }
        public Task RequeueEventAsync(Guid store, int pedido, long inbox, int user, CancellationToken ct = default) => Do(store, pedido, user, null);
        public Task DismissEventAsync(Guid store, int pedido, long inbox, int user, string note, CancellationToken ct = default) => Do(store, pedido, user, note);
        public Task ResolveReviewAsync(Guid store, int pedido, int user, string note, CancellationToken ct = default) => Do(store, pedido, user, note);
        public Task<IReadOnlyList<IfoodAlertRow>> GetStoreAlertsAsync(Guid store, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IfoodAlertRow>>(Alerts.ToList());
        public Task<IfoodPollingHealth?> GetPollingHealthAsync(CancellationToken ct = default) => Task.FromResult(Health);
    }

    private sealed class FakeSettings(bool enabled, bool polling) : IIfoodRuntimeSettingsRepository
    {
        public Task<IfoodRuntimeSettings> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult(new IfoodRuntimeSettings { Enabled = enabled, PollingEnabled = polling });
        public Task UpdateSettingsAsync(string clientId, string? clientSecretCifrado, bool enabled, bool pollingEnabled, bool importEnabled, bool projectionEnabled,
            bool commandsEnabled, bool exceptionsEnabled, string? updatedBy, bool completionEnabled = false, bool retentionEnabled = false, int retentionDays = 30, bool dineInEnabled = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<IfoodConnectionRow>> ListConnectionsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IfoodConnectionRow?> GetStoreConnectionAsync(Guid estabelecimentoId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AuthorizeConnectionAsync(Guid merchantId, Guid estabelecimentoId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RevokeConnectionAsync(Guid merchantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> SetConnectionDeliveryAsync(Guid merchantId, bool enabled, bool applyToOpenOrders = false, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static IfoodDiagnosticsController Controller(FakeReconciliation repository, bool enabled = true, bool polling = true, Guid? store = null, int? user = 7)
    {
        var context = new DefaultHttpContext();
        if (store is Guid s) context.Items["EstabelecimentoId"] = s;
        if (user is int u) context.Items["UserId"] = u;
        return new IfoodDiagnosticsController(repository, new FakeSettings(enabled, polling), new FixedClock(Now))
        { ControllerContext = new ControllerContext { HttpContext = context } };
    }
    private static T Body<T>(IActionResult result, int status = 200)
    {
        var obj = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, obj.StatusCode ?? 200);
        return Assert.IsType<ApiResponse<T>>(obj.Value).Data!;
    }
    private static string? ErrorCode(IActionResult result, int status)
    {
        var obj = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, obj.StatusCode);
        return Assert.IsType<ApiResponse<object>>(obj.Value).Code;
    }

    [Fact]
    public async Task AlertsAddACriticalPollingAlertOnlyWhenPollingIsOnAndStaleForMoreThanTwoMinutes()
    {
        var store = Guid.NewGuid();
        var repository = new FakeReconciliation { Health = new IfoodPollingHealth { NextPollAtUtc = Now.AddMinutes(-3) } };
        var stale = Body<List<IfoodAlertDto>>(await Controller(repository, store: store).Alerts(default));
        var polling = Assert.Single(stale);
        Assert.Equal("IFOOD_POLLING_STALE", polling.Code); Assert.Equal("critical", polling.Severity);

        repository.Health = new IfoodPollingHealth { NextPollAtUtc = Now.AddMinutes(-3), LastErrorCode = "IFOOD_AUTH_FAILED" };
        Assert.Equal("IFOOD_POLLING_ERROR", Assert.Single(Body<List<IfoodAlertDto>>(await Controller(repository, store: store).Alerts(default))).Code);

        repository.Health = new IfoodPollingHealth { NextPollAtUtc = Now.AddSeconds(-90) };   // dentro da tolerância
        Assert.Empty(Body<List<IfoodAlertDto>>(await Controller(repository, store: store).Alerts(default)));
        repository.Health = null;
        Assert.Empty(Body<List<IfoodAlertDto>>(await Controller(repository, store: store).Alerts(default)));

        repository.Health = new IfoodPollingHealth { NextPollAtUtc = Now.AddMinutes(-30) };
        Assert.Empty(Body<List<IfoodAlertDto>>(await Controller(repository, polling: false, store: store).Alerts(default)));  // polling desligado
        Assert.Empty(Body<List<IfoodAlertDto>>(await Controller(repository, enabled: false, store: store).Alerts(default)));  // integração desligada
    }

    [Fact]
    public async Task AlertsKeepTheStoresAlertsAndPutPollingFirst()
    {
        var repository = new FakeReconciliation { Health = new IfoodPollingHealth { NextPollAtUtc = Now.AddMinutes(-5) } };
        repository.Alerts.Add(new IfoodAlertRow { Code = "IFOOD_ORDER_REVIEW", Severity = "warning", Count = 2 });
        var alerts = Body<List<IfoodAlertDto>>(await Controller(repository, store: Guid.NewGuid()).Alerts(default));
        Assert.Equal(new[] { "IFOOD_POLLING_STALE", "IFOOD_ORDER_REVIEW" }, alerts.Select(a => a.Code).ToArray());
        Assert.Equal(2, alerts[1].Count);
    }

    [Fact]
    public async Task ActionsUseTheTokensStoreAndUserAndRejectRequestsWithoutThem()
    {
        var store = Guid.NewGuid();
        var repository = new FakeReconciliation();
        await Controller(repository, store: store, user: 42).Dismiss(100, 9, new IfoodReconciliationNoteRequest { Note = "conferido no portal" }, default);
        Assert.Equal((store, 100, 42, "conferido no portal"), repository.Last);

        Assert.IsType<UnauthorizedResult>(await Controller(repository, store: null).Events(100, default));
        Assert.IsType<UnauthorizedResult>(await Controller(repository, store: store, user: null).Reconcile(100, new IfoodReconciliationNoteRequest(), default));
        Assert.IsType<UnauthorizedResult>(await Controller(repository, store: store, user: 0).Requeue(100, 9, default));
    }

    [Theory]
    [InlineData("IFOOD_RECONCILIATION_NOT_FOUND", 404)]
    [InlineData("IFOOD_RECONCILIATION_NOTE_REQUIRED", 400)]
    [InlineData("IFOOD_RECONCILIATION_INVALID_STATE", 409)]
    [InlineData("IFOOD_RECONCILIATION_EVENTS_PENDING", 409)]
    [InlineData("IFOOD_RECONCILIATION_COMMAND_PENDING", 409)]
    [InlineData("IFOOD_RECONCILIATION_DATA_STALE", 409)]
    [InlineData("IFOOD_RECONCILIATION_PAYLOAD_EXPIRED", 409)]
    public async Task ReconciliationErrorsMapToTheRightStatusAndKeepTheCode(string code, int status)
    {
        var repository = new FakeReconciliation { ThrowCode = code };
        var result = await Controller(repository, store: Guid.NewGuid()).Reconcile(100, new IfoodReconciliationNoteRequest { Note = "conferido no portal" }, default);
        Assert.Equal(code, ErrorCode(result, status));
    }

    [Fact]
    public async Task EventListNeverExposesAPayloadField()
    {
        var events = Body<List<IfoodOrderEventDto>>(await Controller(new FakeReconciliation(), store: Guid.NewGuid()).Events(100, default));
        var dto = Assert.Single(events);
        Assert.Equal("DDCR", dto.EventCode); Assert.Equal("IFOOD_EVENT_CODE_CONFLICT", dto.ReasonCode);
        Assert.DoesNotContain(typeof(IfoodOrderEventDto).GetProperties(), p => p.Name.Contains("Payload", StringComparison.OrdinalIgnoreCase));
    }
}
