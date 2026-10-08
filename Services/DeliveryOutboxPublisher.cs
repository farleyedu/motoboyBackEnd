using System.Diagnostics;
using System.Text.Json;
using APIBack.Hubs;
using APIBack.Options;
using APIBack.Repository.Interface;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace APIBack.Services;

public sealed class DeliveryOutboxPublisher(
    IDeliveryOutboxRepository repository, IHubContext<DeliveryHub> hubContext,
    IOptions<DeliveryTrackingOptions> options, ILogger<DeliveryOutboxPublisher> logger,
    DeliverySyncMetrics? metrics = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Max(250, options.Value.OutboxPollIntervalMilliseconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            if (options.Value.Enabled)
            {
                try { await PublishBatchAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError(ex, "Falha ao publicar outbox do delivery."); }
            }
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal async Task PublishBatchAsync(CancellationToken cancellationToken)
    {
        var leaseId = Guid.NewGuid();
        var seconds = Math.Clamp(options.Value.OutboxLeaseSeconds, 30, 300);
        var renewedAt = Stopwatch.GetTimestamp();
        var records = await repository.ClaimAsync(leaseId, seconds, Math.Clamp(options.Value.OutboxBatchSize, 1, 500), cancellationToken);
        if (records == null) return;
        var blocked = new HashSet<(string Group, int? Motoboy)>();
        try
        {
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (blocked.Contains((record.TargetGroup, record.MotoboyId))) continue;
                if (Stopwatch.GetElapsedTime(renewedAt).TotalSeconds >= seconds / 3.0)
                {
                    if (!await repository.RenewAsync(leaseId, seconds, cancellationToken)) break;
                    renewedAt = Stopwatch.GetTimestamp();
                }
                string? error = null;
                try
                {
                    using var document = JsonDocument.Parse(record.Payload);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.OutboxSendTimeoutSeconds, 1, 10)));
                    // A reserva ja devolveu sua conexao ao pool: aqui so existe trabalho de rede.
                    await hubContext.Clients.Group(record.TargetGroup)
                        .SendAsync(record.EventName, document.RootElement.Clone(), timeout.Token);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    error = ex is OperationCanceledException ? "Timeout ao enviar evento." : ex.Message;
                    blocked.Add((record.TargetGroup, record.MotoboyId));
                    metrics?.RecordPublishFailure();
                    logger.LogWarning("Falha temporaria ao publicar evento {EventId}: {ErrorType}.", record.EventId, ex.GetType().Name);
                }
                if (!await repository.CompleteAsync(leaseId, record.EventId, error, cancellationToken)) break;
                if (error == null) metrics?.RecordPublished(record.OccurredAtUtc);
            }
        }
        finally
        {
            // Em shutdown liberar a reserva; se o banco falhar ela expira sozinha.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await repository.ReleaseAsync(leaseId, cleanup.Token); }
            catch (Exception ex) { logger.LogWarning("Reserva da outbox aguardara expiracao ({ErrorType}).", ex.GetType().Name); }
        }
    }
}
