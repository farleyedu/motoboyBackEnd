using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace APIBack.Payments.InfinitePay.Webhooks
{
    /// <summary>
    /// Processa e recupera avisos InfinitePay recebidos (fila no banco, reserva por SKIP LOCKED: seguro com varias instancias). Reagenda com
    /// backoff exponencial; esgotadas as tentativas abre pendencia. NAO consulta cobrancas so por order_nsu (o /payment_check exige
    /// transaction_nsu e slug): pagamento sem esses dados vira pendencia manual.
    /// </summary>
    public sealed class InfinitePayWebhookWorker : BackgroundService
    {
        private static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(10);
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<InfinitePayWebhookWorker> _logger;

        public InfinitePayWebhookWorker(IServiceScopeFactory scopes, ILogger<InfinitePayWebhookWorker> logger)
        {
            _scopes = scopes;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken); } catch (OperationCanceledException) { return; }
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var processador = scope.ServiceProvider.GetRequiredService<IInfinitePayWebhookProcessor>();
                    int n;
                    do
                    {
                        n = await processador.ProcessarPendentesAsync(20, stoppingToken);
                        if (n > 0) _logger.LogInformation("[ip.webhook] ev=ciclo processados={N}", n);
                    } while (n >= 20 && !stoppingToken.IsCancellationRequested);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                // Migration pendente ou banco fora: tenta no proximo ciclo.
                catch (Exception ex) { _logger.LogWarning("[ip.webhook] ev=ciclo_falhou erro={Erro}", ex.GetType().Name); }
                try { await Task.Delay(Intervalo, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
