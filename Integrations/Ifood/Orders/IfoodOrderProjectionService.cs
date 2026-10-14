using APIBack.Integrations.Ifood.Events;
using Microsoft.Extensions.Options;

namespace APIBack.Integrations.Ifood.Orders;

public sealed class IfoodOrderProjectionService
{
    private readonly IfoodInboxRepository _inbox;
    private readonly IfoodOrderProjectionRepository _repository;
    private readonly IfoodOptions _options;
    public IfoodOrderProjectionService(IfoodInboxRepository inbox, IfoodOrderProjectionRepository repository, IOptions<IfoodOptions> options)
        => (_inbox,_repository,_options)=(inbox,repository,options.Value);
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled || !_options.ProjectionEnabled) return 0;
        var count = 0;
        for (var index=0;index<_options.ProjectionBatchSize;index++)
        {
            var rows = await _inbox.ClaimAsync(1,_options.ProjectionLeaseSeconds,_options.ProjectionMaxAttempts,ct,
                excludedCodes: new[] { "PLC","PLACED" });
            if (rows.Count == 0) break;
            var item=rows[0];
            try { await _repository.ProjectAsync(item,ct); count++; }
            catch (IfoodIntegrationException ex)
            {
                if (ex.Retryable) await _inbox.RetryEventAsync(item,_options.ProjectionMaxAttempts,TimeSpan.FromSeconds(Math.Min(300,30*Math.Pow(2,Math.Min(item.Attempts-1,4)))),ct);
                else await _inbox.FinishEventAsync(item,IfoodInboxOutcome.Review,ct,ex.Code);
            }
        }
        return count;
    }
}
public sealed class IfoodOrderProjectionWorker : BackgroundService
{
    private readonly IfoodOrderProjectionService _service;
    private readonly IfoodOptions _options;
    private readonly ILogger<IfoodOrderProjectionWorker> _logger;
    public IfoodOrderProjectionWorker(IfoodOrderProjectionService service,IOptions<IfoodOptions> options,ILogger<IfoodOrderProjectionWorker> logger)
        => (_service,_options,_logger)=(service,options.Value,logger);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.ProjectionEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await _service.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when(stoppingToken.IsCancellationRequested) { break; }
            catch (IfoodIntegrationException ex) { _logger.LogWarning("Projeção iFood interrompida: {Code}.",ex.Code); }
            catch (Exception) { _logger.LogError("Projeção iFood interrompida: IFOOD_PROJECTION_INTERNAL_ERROR."); }
            try { await Task.Delay(TimeSpan.FromSeconds(5),stoppingToken); }
            catch (OperationCanceledException) when(stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
