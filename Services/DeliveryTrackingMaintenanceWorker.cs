using System;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Options;
using APIBack.Repository.Interface;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace APIBack.Services
{
    public sealed class DeliveryTrackingMaintenanceWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly DeliveryTrackingOptions _options;
        private readonly ILogger<DeliveryTrackingMaintenanceWorker> _logger;
        private DateTimeOffset _nextRetentionRunUtc = DateTimeOffset.UtcNow;

        public DeliveryTrackingMaintenanceWorker(
            IServiceScopeFactory scopeFactory,
            IOptions<DeliveryTrackingOptions> options,
            ILogger<DeliveryTrackingMaintenanceWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var interval = TimeSpan.FromSeconds(Math.Clamp(_options.ExpirationWorkerIntervalSeconds, 5, 60));
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_options.Enabled)
                {
                    try
                    {
                        await RunMaintenanceAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Falha na manutencao de sessoes do delivery.");
                    }
                }

                await Task.Delay(interval, stoppingToken);
            }
        }

        private async Task RunMaintenanceAsync()
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IOperationalSessionRepository>();
            await repository.ExpireDueSessionsAsync(500);

            if (_nextRetentionRunUtc > DateTimeOffset.UtcNow)
            {
                return;
            }

            var threshold = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, _options.LocationRetentionDays));
            int removed;
            do
            {
                removed = await repository.DeleteOldLocationsAsync(threshold, 5000);
            } while (removed == 5000);

            _nextRetentionRunUtc = DateTimeOffset.UtcNow.AddHours(24);
        }
    }
}

