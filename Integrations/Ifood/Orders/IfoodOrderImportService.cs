using APIBack.Integrations.Ifood.Events;
using Microsoft.Extensions.Options;

namespace APIBack.Integrations.Ifood.Orders;

public sealed class IfoodOrderImportService
{
    private readonly IfoodInboxRepository _inbox;
    private readonly IfoodOrderImportRepository _orders;
    private readonly IIfoodOrderDetailsClient _details;
    private readonly IfoodOptions _options;
    public IfoodOrderImportService(IfoodInboxRepository inbox, IfoodOrderImportRepository orders, IIfoodOrderDetailsClient details, IOptions<IfoodOptions> options)
        => (_inbox, _orders, _details, _options) = (inbox, orders, details, options.Value);

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled || !_options.ImportEnabled) return 0;
        // Claims individuais: nenhum evento espera na memória consumindo sua reserva durante HTTP de outro pedido.
        var count = 0;
        for (var index = 0; index < _options.ImportBatchSize; index++)
        {
            var rows = await _inbox.ClaimAsync(1, _options.ImportLeaseSeconds, _options.ImportMaxAttempts, ct, new[] { "PLC", "PLACED" });
            if (rows.Count == 0) break;
            var item = rows[0];
            IfoodDetailsReservation? reservation = null;
            try
            {
                reservation = await _orders.ReserveDetailsAsync(item, _options.ImportLeaseSeconds, ct);
                if (reservation == null) throw new IfoodIntegrationException("IFOOD_DETAILS_BUSY", retryable: true);
                var payload = reservation.PayloadJson;
                if (payload == null)
                {
                    payload = await _details.GetAsync(reservation, async token =>
                    {
                        if (!await _inbox.RenewEventAsync(item, _options.ImportLeaseSeconds, token))
                            throw new IfoodIntegrationException("IFOOD_EVENT_LEASE_LOST", retryable: true);
                        await _orders.RenewDetailsAsync(reservation, _options.ImportLeaseSeconds, token);
                    }, ct);
                    // Cache durável separado da projeção: falha posterior não repete GET já salvo.
                    await _orders.StoreDetailsAsync(reservation, payload, ct);
                }
                if (!await _inbox.RenewEventAsync(item, _options.ImportLeaseSeconds, ct))
                    throw new IfoodIntegrationException("IFOOD_EVENT_LEASE_LOST", retryable: true);
                await _orders.ImportAsync(item, IfoodOrderNormalizer.Normalize(payload, item.MerchantId, item.ExternalOrderId!.Value), ct);
                count++;
            }
            catch (IfoodIntegrationException ex)
            {
                var delay = ex.RetryAfter is { } retry && retry > TimeSpan.FromSeconds(30) ? retry : TimeSpan.FromSeconds(Math.Min(300, 30 * Math.Pow(2, Math.Min(item.Attempts - 1, 4))));
                if (reservation?.LeaseId != null) await _orders.DetailsFailedAsync(reservation, ex.Code, delay, ct);
                if (ex.Retryable) await _inbox.RetryEventAsync(item, _options.ImportMaxAttempts, delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay, ct);
                else await _inbox.FinishEventAsync(item, IfoodInboxOutcome.Review, ct, ex.Code);
            }
            // Cancelamento/crash: reservas expiram; cache e efeito transacional são recuperáveis.
        }
        return count;
    }
}

public sealed class IfoodOrderImportWorker : BackgroundService
{
    private readonly IfoodOrderImportService _service;
    private readonly IfoodOptions _options;
    private readonly ILogger<IfoodOrderImportWorker> _logger;
    public IfoodOrderImportWorker(IfoodOrderImportService service, IOptions<IfoodOptions> options, ILogger<IfoodOrderImportWorker> logger)
        => (_service, _options, _logger) = (service, options.Value, logger);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.ImportEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await _service.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (IfoodIntegrationException ex) { _logger.LogWarning("Importação iFood interrompida: {Code}.", ex.Code); }
            catch (Exception) { _logger.LogError("Importação iFood interrompida: IFOOD_IMPORT_INTERNAL_ERROR."); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
