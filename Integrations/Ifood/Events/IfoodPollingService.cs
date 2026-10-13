using Microsoft.Extensions.Options;

namespace APIBack.Integrations.Ifood.Events;

public sealed class IfoodPollingService
{
    private readonly IIfoodInboxRepository _inbox;
    private readonly IIfoodEventsClient _events;
    private readonly IfoodOptions _options;
    private readonly TimeProvider _clock;
    public IfoodPollingService(IIfoodInboxRepository inbox, IIfoodEventsClient events, IOptions<IfoodOptions> options, TimeProvider clock)
        => (_inbox, _events, _options, _clock) = (inbox, events, options.Value, clock);

    public async Task<TimeSpan> RunOnceAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled || !_options.PollingEnabled) return TimeSpan.FromSeconds(30);
        var lease = await _inbox.AcquirePollingAsync(IfoodInboxRepository.ApplicationKey(_options.ClientId), _options.PollingLeaseSeconds, ct);
        if (lease == null) return TimeSpan.FromSeconds(1);
        try
        {
            var incoming = await _events.PollAsync(_options.PollingMerchants,
                token => _inbox.StartPollingRequestAsync(lease, _options.PollingLeaseSeconds, token), ct);
            // Gravação/commit concluídos antes de qualquer ACK, inclusive duplicados.
            await _inbox.PersistAsync(lease, _options.PollingMerchants, incoming, ct);
            // Recupera ACKs pendentes mesmo quando polling responde 204. Limita duração da rodada.
            for (var batch = 0; batch < 5; batch++)
            {
                var pending = await _inbox.PendingAcknowledgmentsAsync(lease, _options.PollingMerchants, _options.AckBatchSize, ct);
                if (pending.Count == 0) break;
                try { await _events.AcknowledgeAsync(pending,
                    token => _inbox.RenewPollingAsync(lease, _options.PollingLeaseSeconds, token), ct); }
                catch (IfoodIntegrationException ex)
                {
                    await _inbox.AcknowledgmentFailedAsync(lease, pending, ex.Code,
                        ex.RetryAfter is { } delay && delay > TimeSpan.FromSeconds(30) ? delay : TimeSpan.FromSeconds(30), ct);
                    throw;
                }
                await _inbox.AcknowledgedAsync(lease, pending, ct);
            }
            var next = await _inbox.ReleasePollingAsync(lease, TimeSpan.Zero, null, ct);
            return TimeSpan.FromSeconds(Math.Max(1, (next - _clock.GetUtcNow()).TotalSeconds));
        }
        catch (IfoodIntegrationException ex)
        {
            var delay = ex.RetryAfter is { } retry && retry > TimeSpan.FromSeconds(30) ? retry : TimeSpan.FromSeconds(30);
            try { await _inbox.ReleasePollingAsync(lease, delay, ex.Code, ct); }
            catch (IfoodIntegrationException) { /* reserva expira; nunca apaga eventos para liberar trabalho */ }
            throw;
        }
        // Cancelamento/crash deixam reserva expirar; dados e ACKs pendentes continuam no banco.
    }
}

public sealed class IfoodPollingWorker : BackgroundService
{
    private readonly IfoodPollingService _polling;
    private readonly IfoodOptions _options;
    private readonly ILogger<IfoodPollingWorker> _logger;
    public IfoodPollingWorker(IfoodPollingService polling, IOptions<IfoodOptions> options, ILogger<IfoodPollingWorker> logger)
        => (_polling, _options, _logger) = (polling, options.Value, logger);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.PollingEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(30);
            try { delay = await _polling.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (IfoodIntegrationException ex)
            {
                _logger.LogWarning("Polling iFood interrompido: {Code}; status {Status}.", ex.Code, ex.RemoteStatus);
                if (ex.RetryAfter is { } retry && retry > delay) delay = retry;
            }
            catch (Exception) { _logger.LogError("Polling iFood interrompido: IFOOD_POLLING_INTERNAL_ERROR."); }
            // Nenhum corpo, token, merchant ou exceção original entra no log.
            try { await Task.Delay(delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
