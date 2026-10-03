using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento
{
    /// <summary>
    /// Le a fila wa_evento (Postgres) e processa um evento por vez, na ordem em que chegaram. O que falha volta para a
    /// fila com espera crescente (15 s, 30 s, 60 s, 2 min) e, depois de 5 tentativas, fica como "erro" para revisao;
    /// um reinicio no meio nunca perde evento: o que estava "processando" volta para a fila.
    /// </summary>
    public sealed class WaEventoWorker : BackgroundService
    {
        private const int MaximoDeTentativas = 5;
        private static readonly TimeSpan Ocioso = TimeSpan.FromMilliseconds(750);
        private static readonly TimeSpan PresoApos = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan IntervaloDeRecuperacao = TimeSpan.FromSeconds(30);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<WaEventoWorker> _logger;
        private DateTime _ultimaRecuperacao = DateTime.MinValue;

        public WaEventoWorker(IServiceScopeFactory scopeFactory, ILogger<WaEventoWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[wa.worker] ev=iniciado");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var trabalhou = await ProcessarLoteAsync(stoppingToken);
                    if (!trabalhou) await Task.Delay(Ocioso, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Banco fora do ar ou migration pendente: o worker nao pode morrer, tenta de novo daqui a pouco.
                    _logger.LogError(ex, "[wa.worker] ev=erro motivo=falha_ao_ler_fila");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }

        private async Task<bool> ProcessarLoteAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var fila = scope.ServiceProvider.GetRequiredService<IWaEventoRepository>();

            if (DateTime.UtcNow - _ultimaRecuperacao > IntervaloDeRecuperacao)
            {
                _ultimaRecuperacao = DateTime.UtcNow;
                var presos = await fila.RecuperarPresosAsync(PresoApos);
                if (presos > 0) _logger.LogWarning("[wa.worker] ev=recuperados quantidade={Quantidade}", presos);
            }

            var lote = await fila.ReivindicarAsync(10);
            if (lote.Count == 0) return false;

            foreach (var evento in lote)
            {
                ct.ThrowIfCancellationRequested();

                // Um escopo por evento: um erro de banco num evento nao contamina o proximo.
                using var escopoDoEvento = _scopeFactory.CreateScope();
                var processador = escopoDoEvento.ServiceProvider.GetRequiredService<IWaEventoProcessor>();
                var repositorio = escopoDoEvento.ServiceProvider.GetRequiredService<IWaEventoRepository>();

                try
                {
                    var resultado = await processador.ProcessarAsync(evento);
                    switch (resultado.Resultado)
                    {
                        case "ignorado":
                            await repositorio.MarcarIgnoradoAsync(evento.Id, resultado.Motivo ?? "ignorado");
                            break;
                        case "tentar_de_novo":
                            await repositorio.ReagendarAsync(evento.Id, resultado.Motivo ?? "aguardando", TimeSpan.FromSeconds(3), definitivo: false);
                            break;
                        default:
                            await repositorio.MarcarProcessadoAsync(evento.Id);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    var definitivo = evento.Tentativas >= MaximoDeTentativas;
                    var espera = TimeSpan.FromSeconds(15 * Math.Pow(2, Math.Max(0, evento.Tentativas - 1)));
                    _logger.LogError(
                        ex,
                        "[wa.worker] ev=falhou evento={Evento} tipo={Tipo} wa={Wa} tentativa={Tentativa} definitivo={Definitivo} proxima_em_s={Espera}",
                        evento.Id, evento.Tipo, evento.Chave, evento.Tentativas, definitivo, definitivo ? 0 : espera.TotalSeconds);
                    await repositorio.ReagendarAsync(evento.Id, ex.Message, espera, definitivo);
                }
            }

            return true;
        }
    }
}
