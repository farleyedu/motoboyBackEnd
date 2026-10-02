using System;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Repository.Interface;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace APIBack.Service
{
    /// <summary>
    /// Recusa sozinha as ofertas de rota que o motoboy nao respondeu no prazo (padrao: 2 minutos),
    /// devolvendo os pedidos a Pendente. Desligavel por configuracao (Oferta:WorkerEnabled).
    /// </summary>
    public sealed class OfertaRotaWorker : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
        private readonly IServiceScopeFactory _scopes;
        private readonly IConfiguration _configuration;
        private readonly ILogger<OfertaRotaWorker> _logger;

        public OfertaRotaWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<OfertaRotaWorker> logger)
        {
            _scopes = scopes;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_configuration.GetValue("Oferta:WorkerEnabled", true))
            {
                _logger.LogInformation("Prazo das ofertas de rota desligado por configuracao (Oferta:WorkerEnabled).");
                return;
            }

            // Espera a subida (migrations) antes da primeira passada.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    var handled = await scope.ServiceProvider.GetRequiredService<IPedidoQueueRepository>().ExpireOffersAsync(DateTimeOffset.UtcNow);
                    if (handled > 0)
                    {
                        _logger.LogInformation("Ofertas de rota sem resposta: {Handled} motoboy(s) tiveram a rota recusada.", handled);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Passada do prazo das ofertas de rota falhou.");
                }

                try
                {
                    await Task.Delay(Interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
