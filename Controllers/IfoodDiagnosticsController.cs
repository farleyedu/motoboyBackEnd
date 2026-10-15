using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Config;
using APIBack.Integrations.Ifood.Reconciliation;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

public sealed class IfoodOrderEventDto
{
    public long InboxId { get; set; }
    public string EventCode { get; set; } = "";
    public string State { get; set; } = "";
    public string? LastErrorCode { get; set; }
    public DateTimeOffset? OccurredAtUtc { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public string? Outcome { get; set; }
    public string? ReasonCode { get; set; }
}

public sealed class IfoodAlertDto
{
    public string Code { get; set; } = "";
    public string Severity { get; set; } = "warning";
    public int Count { get; set; }
    public DateTimeOffset? OldestAtUtc { get; set; }
    public int? PedidoId { get; set; }
    public string? DisplayNumber { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}

public sealed class IfoodReconciliationNoteRequest
{
    public string Note { get; set; } = "";
}

// Diagnóstico e conciliação do operador, sempre restritos à loja do token. Nunca devolvem o corpo bruto dos eventos
// (pode ter dados de cliente) e toda ação manual fica registrada em ifood_reconciliation_audit.
[ApiController, Route("api/v2/delivery/ifood"), RequirePermission("Delivery", "integracao_ifood")]
public sealed class IfoodDiagnosticsController(IIfoodReconciliationRepository repository, IIfoodRuntimeSettingsRepository settings, TimeProvider clock) : ControllerBase
{
    private static readonly TimeSpan PollingStaleAfter = TimeSpan.FromMinutes(2);

    [HttpGet("pedidos/{pedido:int}/events")]
    public Task<IActionResult> Events(int pedido, CancellationToken ct) => Run(async (store, _) =>
        (await repository.ListOrderEventsAsync(store, pedido, ct)).Select(row => new IfoodOrderEventDto
        {
            InboxId = row.InboxId, EventCode = row.EventCode, State = row.State, LastErrorCode = row.LastErrorCode,
            OccurredAtUtc = row.OccurredAtUtc, ReceivedAtUtc = row.ReceivedAtUtc, ProcessedAtUtc = row.ProcessedAtUtc,
            Outcome = row.Outcome, ReasonCode = row.ReasonCode
        }).ToList());

    [HttpPost("pedidos/{pedido:int}/events/{inbox:long}/requeue")]
    public Task<IActionResult> Requeue(int pedido, long inbox, CancellationToken ct) => Run(async (store, user) =>
    { await repository.RequeueEventAsync(store, pedido, inbox, user, ct); return new { }; });

    [HttpPost("pedidos/{pedido:int}/events/{inbox:long}/dismiss"), RequestSizeLimit(4_096)]
    public Task<IActionResult> Dismiss(int pedido, long inbox, IfoodReconciliationNoteRequest request, CancellationToken ct) => Run(async (store, user) =>
    { await repository.DismissEventAsync(store, pedido, inbox, user, request.Note, ct); return new { }; });

    [HttpPost("pedidos/{pedido:int}/reconcile"), RequestSizeLimit(4_096)]
    public Task<IActionResult> Reconcile(int pedido, IfoodReconciliationNoteRequest request, CancellationToken ct) => Run(async (store, user) =>
    { await repository.ResolveReviewAsync(store, pedido, user, request.Note, ct); return new { }; });

    // Alertas operacionais da loja: prazo de aceite, revisão, fila de eventos, falhas e operações incertas;
    // mais a saúde do polling (global, sem dados de outras lojas).
    [HttpGet("alerts")]
    public Task<IActionResult> Alerts(CancellationToken ct) => Run(async (store, _) =>
    {
        var alerts = (await repository.GetStoreAlertsAsync(store, ct)).Select(row => new IfoodAlertDto
        { Code = row.Code, Severity = row.Severity, Count = row.Count, OldestAtUtc = row.OldestAtUtc,
          PedidoId = row.PedidoId, DisplayNumber = row.DisplayNumber, ExpiresAtUtc = row.ExpiresAtUtc }).ToList();
        var config = await settings.GetSettingsAsync(ct);
        if (config.Enabled && config.PollingEnabled)
        {
            var health = await repository.GetPollingHealthAsync(ct);
            if (health?.NextPollAtUtc is { } next && clock.GetUtcNow() - next > PollingStaleAfter)
                alerts.Insert(0, new IfoodAlertDto { Code = health.LastErrorCode is { Length: > 0 } ? "IFOOD_POLLING_ERROR" : "IFOOD_POLLING_STALE", Severity = "critical", Count = 1, OldestAtUtc = next });
        }
        return alerts;
    });

    private async Task<IActionResult> Run<T>(Func<Guid, int, Task<T>> action)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetEstabelecimentoId() is not Guid store || HttpContext.GetUserId() is not int user || user <= 0) return Unauthorized();
        try { return Ok(ApiResponse<T>.Ok(await action(store, user))); }
        catch (IfoodIntegrationException ex)
        {
            var status = ex.Code switch
            {
                "IFOOD_RECONCILIATION_NOT_FOUND" => 404,
                "IFOOD_RECONCILIATION_NOTE_REQUIRED" => 400,
                _ => 409
            };
            return StatusCode(status, ApiResponse<object>.Fail("Não foi possível concluir a conciliação.", ex.Code));
        }
    }
}
