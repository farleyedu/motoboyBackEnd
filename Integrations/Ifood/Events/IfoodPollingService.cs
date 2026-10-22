using System.Net;
using APIBack.Integrations.Ifood.Connections;
using Microsoft.Extensions.Options;

namespace APIBack.Integrations.Ifood.Events;

public sealed class IfoodPollingService
{
    private readonly IIfoodInboxRepository _inbox;
    private readonly IIfoodEventsClient _events;
    private readonly IOptionsMonitor<IfoodOptions> _optionsMonitor;
    private IfoodOptions _options => _optionsMonitor.CurrentValue;
    private readonly TimeProvider _clock;
    private readonly IIfoodConnectionHealth _health;
    public IfoodPollingService(IIfoodInboxRepository inbox, IIfoodEventsClient events, IOptionsMonitor<IfoodOptions> options, TimeProvider clock,
        IIfoodConnectionHealth? health = null)
        => (_inbox, _events, _optionsMonitor, _clock, _health) = (inbox, events, options, clock, health ?? NullIfoodConnectionHealth.Instance);

    // Erro que o iFood atribui a UM merchant (sem permissao, nao encontrado, pedido invalido): nao pode parar os demais.
    // Credencial do app (401), limite (429), 5xx e rede afetam todos e continuam subindo para o backoff global.
    public static bool IsMerchantScoped(IfoodIntegrationException ex)
        => ex.Code is "IFOOD_EVENTS_FORBIDDEN" or "IFOOD_MERCHANT_NOT_AUTHORIZED" ||
           (ex.Code == "IFOOD_EVENTS_REMOTE_ERROR" && ex.RemoteStatus is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity);

    public async Task<TimeSpan> RunOnceAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled || !_options.PollingEnabled) return TimeSpan.FromSeconds(30);
        var lease = await _inbox.AcquirePollingAsync(IfoodInboxRepository.ApplicationKey(_options.ClientId), _options.PollingLeaseSeconds, ct);
        if (lease == null) return TimeSpan.FromSeconds(1);
        try
        {
            var merchants = _options.PollingMerchants.ToList();
            var blocked = await _health.BlockedAsync(merchants.Select(m => m.MerchantId).ToArray(), ct);
            var now = _clock.GetUtcNow();
            // Em quarentena e ainda dentro do prazo: nao entra no polling. Vencido: volta, mas sozinho (revalidacao).
            var healthy = merchants.Where(m => !blocked.ContainsKey(m.MerchantId)).ToList();
            var retrying = merchants.Where(m => blocked.TryGetValue(m.MerchantId, out var until) && until <= now).ToList();

            var polled = new List<IfoodPollingMerchant>();
            var incoming = new List<IfoodEventEnvelope>();
            try
            {
                if (healthy.Count > 0) await PollGroupAsync(lease, healthy, polled, incoming, ct);
                foreach (var merchant in retrying) await PollOneAsync(lease, merchant, polled, incoming, ct);
            }
            catch (IfoodIntegrationException) when (incoming.Count > 0)
            {
                // Falha global (429, 5xx, rede) depois de alguns merchants ja terem respondido: grava o que chegou.
                try { await _inbox.PersistAsync(lease, polled, incoming, ct); } catch (IfoodIntegrationException) { /* o iFood reenvia o que nao teve ACK */ }
                throw;
            }

            // Gravação/commit concluídos antes de qualquer ACK, inclusive duplicados.
            if (polled.Count > 0 || incoming.Count > 0) await _inbox.PersistAsync(lease, polled, incoming, ct);
            await _health.RecordPollSuccessAsync(polled.Select(m => m.MerchantId).ToArray(), ct);
            await AcknowledgePendingAsync(lease, ct);
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

    // Tenta todos juntos (uma chamada). Se o iFood recusar por causa de um merchant, separa um a um: quem falha vai para a
    // quarentena e os demais continuam recebendo. Nada foi consumido numa chamada recusada, entao repetir nao perde evento.
    private async Task PollGroupAsync(IfoodPollingLease lease, List<IfoodPollingMerchant> group,
        List<IfoodPollingMerchant> polled, List<IfoodEventEnvelope> incoming, CancellationToken ct)
    {
        try
        {
            incoming.AddRange(await _events.PollAsync(group, token => _inbox.StartPollingRequestAsync(lease, _options.PollingLeaseSeconds, token), ct));
            polled.AddRange(group);
        }
        catch (IfoodIntegrationException ex) when (IsMerchantScoped(ex))
        {
            if (group.Count == 1) { await _health.RecordPollFailureAsync(group[0].MerchantId, ex.Code, ct); return; }
            foreach (var merchant in group) await PollOneAsync(lease, merchant, polled, incoming, ct);
        }
    }

    private async Task PollOneAsync(IfoodPollingLease lease, IfoodPollingMerchant merchant,
        List<IfoodPollingMerchant> polled, List<IfoodEventEnvelope> incoming, CancellationToken ct)
    {
        try
        {
            incoming.AddRange(await _events.PollAsync(new[] { merchant }, token => _inbox.StartPollingRequestAsync(lease, _options.PollingLeaseSeconds, token), ct));
            polled.Add(merchant);
        }
        catch (IfoodIntegrationException ex) when (IsMerchantScoped(ex))
        {
            await _health.RecordPollFailureAsync(merchant.MerchantId, ex.Code, ct);
        }
    }

    // Recupera ACKs pendentes mesmo quando polling responde 204. Limita duração da rodada. Um merchant por chamada:
    // a recusa de um não impede o ACK dos outros.
    private async Task AcknowledgePendingAsync(IfoodPollingLease lease, CancellationToken ct)
    {
        for (var batch = 0; batch < 5; batch++)
        {
            var pending = await _inbox.PendingAcknowledgmentsAsync(lease, _options.PollingMerchants, _options.AckBatchSize, ct);
            if (pending.Count == 0) break;
            foreach (var group in pending.GroupBy(e => e.MerchantId))
            {
                var receipts = group.ToArray();
                try
                {
                    await _events.AcknowledgeAsync(receipts, token => _inbox.RenewPollingAsync(lease, _options.PollingLeaseSeconds, token), ct);
                }
                catch (IfoodIntegrationException ex)
                {
                    await _inbox.AcknowledgmentFailedAsync(lease, receipts, ex.Code,
                        ex.RetryAfter is { } delay && delay > TimeSpan.FromSeconds(30) ? delay : TimeSpan.FromSeconds(30), ct);
                    if (!IsMerchantScoped(ex)) throw;
                    continue;
                }
                await _inbox.AcknowledgedAsync(lease, receipts, ct);
            }
        }
    }
}

public sealed class IfoodPollingWorker : BackgroundService
{
    private readonly IfoodPollingService _polling;
    private readonly IOptionsMonitor<IfoodOptions> _options;
    private readonly ILogger<IfoodPollingWorker> _logger;
    public IfoodPollingWorker(IfoodPollingService polling, IOptionsMonitor<IfoodOptions> options, ILogger<IfoodPollingWorker> logger)
        => (_polling, _options, _logger) = (polling, options, logger);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_options.CurrentValue.Enabled || !_options.CurrentValue.PollingEnabled)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                continue;
            }
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
