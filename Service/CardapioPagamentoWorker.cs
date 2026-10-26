using System;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Service.Interface;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace APIBack.Service
{
    /// <summary>
    /// A cada minuto: devolve o dinheiro de quem pagou e nao confirmou o numero no prazo e chama o Mercado Pago para os reembolsos
    /// pendentes (gravados na mesma transacao da recusa/cancelamento/webhook). Varias instancias podem rodar: a reserva usa SKIP LOCKED
    /// e o estorno tem chave de idempotencia.
    /// </summary>
    public sealed class CardapioPagamentoWorker : BackgroundService
    {
        private static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(60);
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<CardapioPagamentoWorker> _logger;

        public CardapioPagamentoWorker(IServiceScopeFactory scopes, ILogger<CardapioPagamentoWorker> logger)
        {
            _scopes = scopes;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    var pagamentos = scope.ServiceProvider.GetRequiredService<ICardapioPagamentoService>();
                    await pagamentos.DevolverAbandonadosAsync(stoppingToken);
                    await pagamentos.ProcessarReembolsosAsync(null, stoppingToken);
                }
                catch (DeliveryDomainException ex) when (ex.Code == "MIGRATION_PENDING")
                {
                    // Sem a migration 20261101_30 nao ha pagamento online a cuidar.
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "[cardapio.pag] ev=trabalhador_falhou");
                }

                await Task.Delay(Intervalo, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
