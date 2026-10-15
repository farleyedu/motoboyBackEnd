using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.Integrations.Ifood.Retention;

// Limpeza automática, ligada e dimensionada pela tela (nunca por variável de ambiente). Só remove o corpo bruto de eventos
// já processados e confirmados, e rejeições antigas. Pedidos, histórico, auditoria e vínculos nunca são apagados aqui.
public sealed class IfoodRetentionService
{
    public const int BatchSize = 1000;
    private readonly NpgsqlDataSource _source;
    private readonly IOptionsMonitor<IfoodOptions> _options;
    public IfoodRetentionService(NpgsqlDataSource source, IOptionsMonitor<IfoodOptions> options) => (_source, _options) = (source, options);

    public sealed record Result(int InboxPayloadsCleared, int RejectionsDeleted)
    {
        public bool BatchFull => InboxPayloadsCleared >= BatchSize || RejectionsDeleted >= BatchSize;
    }

    public async Task<Result> RunOnceAsync(CancellationToken ct = default)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled || !options.RetentionEnabled || options.RetentionDays is < 7 or > 365) return new Result(0, 0);
        await using var connection = await _source.OpenConnectionAsync(ct);
        var cleared = await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_events_inbox SET payload='{}'::jsonb
WHERE id IN (
    SELECT id FROM ifood_events_inbox
    WHERE state IN ('processed','ignored') AND acknowledged_at_utc IS NOT NULL
      AND received_at_utc < NOW() - make_interval(days => @Days) AND payload <> '{}'::jsonb
    ORDER BY id LIMIT @Batch)
""", new { Days = options.RetentionDays, Batch = BatchSize }, cancellationToken: ct));
        var rejections = await connection.ExecuteAsync(new CommandDefinition("""
DELETE FROM ifood_event_rejections
WHERE ctid IN (SELECT ctid FROM ifood_event_rejections WHERE received_at_utc < NOW() - make_interval(days => @Days) LIMIT @Batch)
""", new { Days = options.RetentionDays, Batch = BatchSize }, cancellationToken: ct));
        return new Result(cleared, rejections);
    }
}

public sealed class IfoodRetentionWorker : BackgroundService
{
    private readonly IfoodRetentionService _service;
    private readonly IOptionsMonitor<IfoodOptions> _options;
    private readonly ILogger<IfoodRetentionWorker> _logger;
    public IfoodRetentionWorker(IfoodRetentionService service, IOptionsMonitor<IfoodOptions> options, ILogger<IfoodRetentionWorker> logger)
        => (_service, _options, _logger) = (service, options, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(60);
            try
            {
                var options = _options.CurrentValue;
                if (options.Enabled && options.RetentionEnabled)
                {
                    var result = await _service.RunOnceAsync(stoppingToken);
                    // Lote cheio: continua logo; senão repete a cada hora.
                    delay = result.BatchFull ? TimeSpan.FromSeconds(5) : TimeSpan.FromHours(1);
                    if (result.InboxPayloadsCleared + result.RejectionsDeleted > 0)
                        _logger.LogInformation("Retenção iFood: {Inbox} corpos de evento e {Rejections} rejeições removidos.", result.InboxPayloadsCleared, result.RejectionsDeleted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { _logger.LogError("Retenção iFood interrompida: IFOOD_RETENTION_INTERNAL_ERROR."); }
            try { await Task.Delay(delay, stoppingToken); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
