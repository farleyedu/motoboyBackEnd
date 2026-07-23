using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Hubs;
using APIBack.Model.Tracking;
using APIBack.Options;
using Dapper;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.Services
{
    public sealed class DeliveryOutboxPublisher : BackgroundService
    {
        private const long PublisherAdvisoryLock = 75442201;
        private readonly NpgsqlDataSource _dataSource;
        private readonly IHubContext<DeliveryHub> _hubContext;
        private readonly DeliveryTrackingOptions _options;
        private readonly ILogger<DeliveryOutboxPublisher> _logger;

        public DeliveryOutboxPublisher(
            NpgsqlDataSource dataSource,
            IHubContext<DeliveryHub> hubContext,
            IOptions<DeliveryTrackingOptions> options,
            ILogger<DeliveryOutboxPublisher> logger)
        {
            _dataSource = dataSource;
            _hubContext = hubContext;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var delay = TimeSpan.FromMilliseconds(Math.Max(250, _options.OutboxPollIntervalMilliseconds));
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_options.Enabled)
                {
                    try
                    {
                        await PublishBatchAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Falha ao publicar outbox do delivery.");
                    }
                }

                await Task.Delay(delay, stoppingToken);
            }
        }

        private async Task PublishBatchAsync(CancellationToken cancellationToken)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var ownsLock = await connection.ExecuteScalarAsync<bool>(
                "SELECT pg_try_advisory_xact_lock(@LockId);",
                new { LockId = PublisherAdvisoryLock },
                transaction);
            if (!ownsLock)
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            var records = (await connection.QueryAsync<DeliveryOutboxRecord>(@"
SELECT event_id AS EventId,
       event_name AS EventName,
       target_group AS TargetGroup,
       payload::text AS Payload
  FROM delivery_realtime_outbox
 WHERE published_at_utc IS NULL
   AND next_attempt_at_utc <= NOW()
 ORDER BY occurred_at_utc, event_id
 FOR UPDATE SKIP LOCKED
 LIMIT @Limit;",
                new { Limit = Math.Clamp(_options.OutboxBatchSize, 1, 500) },
                transaction)).ToArray();

            foreach (var record in records)
            {
                try
                {
                    using var document = JsonDocument.Parse(record.Payload);
                    await _hubContext.Clients
                        .Group(record.TargetGroup)
                        .SendAsync(record.EventName, document.RootElement.Clone(), cancellationToken);
                    await connection.ExecuteAsync(@"
UPDATE delivery_realtime_outbox
   SET published_at_utc = NOW(),
       attempts = attempts + 1,
       last_error = NULL
 WHERE event_id = @EventId;", new { record.EventId }, transaction);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await connection.ExecuteAsync(@"
UPDATE delivery_realtime_outbox
   SET attempts = attempts + 1,
       last_error = LEFT(@Error, 2000),
       next_attempt_at_utc = NOW() + (LEAST(attempts + 1, 60) * INTERVAL '1 second')
 WHERE event_id = @EventId;",
                        new { record.EventId, Error = ex.Message },
                        transaction);
                    _logger.LogWarning(ex, "Falha temporaria ao publicar evento {EventId}.", record.EventId);
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
    }
}

