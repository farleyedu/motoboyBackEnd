using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Encerra os pedidos que sobraram em aberto: do expediente anterior, algumas horas depois do
    /// fechamento do estabelecimento, ou todos de uma vez quando o atendente encerra o expediente.
    /// Cada pedido e encerrado na sua propria transacao: um que falhe nao trava os demais, e a
    /// proxima passada tenta de novo.
    /// </summary>
    public sealed class EncerramentoService
    {
        private readonly IPedidoQueueRepository _queue;
        private readonly IHorarioOperacaoRepository _horarios;
        private readonly ITrackingRepository _tracking;
        private readonly ILogger<EncerramentoService> _logger;

        public EncerramentoService(
            IPedidoQueueRepository queue,
            IHorarioOperacaoRepository horarios,
            ITrackingRepository tracking,
            ILogger<EncerramentoService> logger)
        {
            _queue = queue;
            _horarios = horarios;
            _tracking = tracking;
            _logger = logger;
        }

        /// <summary>Uma passada do worker: todos os estabelecimentos com pedido em aberto.</summary>
        public async Task<EncerramentoResultDto> RunOnceAsync(DateTimeOffset agoraUtc)
        {
            var total = new EncerramentoResultDto();
            foreach (var estabelecimentoId in await _queue.ListEstablishmentsWithOpenOrdersAsync())
            {
                try
                {
                    var result = await EncerrarExpedienteAnteriorAsync(estabelecimentoId, agoraUtc);
                    total.Encerrados += result.Encerrados;
                    total.Falhas += result.Falhas;
                }
                catch (Exception ex)
                {
                    total.Falhas++;
                    _logger.LogWarning(ex, "Encerramento automatico falhou para o estabelecimento {EstabelecimentoId}.", estabelecimentoId);
                }
            }
            return total;
        }

        /// <summary>Encerra o que sobrou do expediente que fechou ha mais de N horas (N configuravel por estabelecimento).</summary>
        public async Task<EncerramentoResultDto> EncerrarExpedienteAnteriorAsync(Guid estabelecimentoId, DateTimeOffset agoraUtc)
        {
            var result = new EncerramentoResultDto();
            var settings = await _queue.GetEncerramentoSettingsAsync(estabelecimentoId);
            if (!settings.Ativo) return result;

            var tz = EncerramentoRules.ResolveTimeZone(await _tracking.GetEstablishmentTimezoneAsync(estabelecimentoId));
            var hoje = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(agoraUtc, tz).DateTime);
            var fechamentos = new Dictionary<DateOnly, TimeSpan?>();
            for (var recuo = 0; recuo <= 3; recuo++)
            {
                var dia = hoje.AddDays(-recuo);
                fechamentos[dia] = await _horarios.ObterHoraFechamentoAsync(estabelecimentoId, dia);
            }

            var corte = EncerramentoRules.UltimoFechamentoElegivel(
                dia => fechamentos.TryGetValue(dia, out var hora) ? hora : null, agoraUtc, tz, settings.Horas);
            if (corte is null) return result;

            var candidatos = (await _queue.ListOpenOrdersForEncerramentoAsync(estabelecimentoId))
                .Where(c => EncerramentoRules.PertenceAoExpedienteEncerrado(c.FeitoEmUtc, corte.Value))
                .ToList();
            await EncerrarAsync(estabelecimentoId, candidatos.Select(c => c.Id), EncerramentoRules.MotivoExpediente, null, result);
            return result;
        }

        /// <summary>O atendente encerrou o expediente: todo pedido ainda em aberto e encerrado agora.</summary>
        public async Task<EncerramentoResultDto> EncerrarExpedienteAgoraAsync(Guid estabelecimentoId, int actorUserId)
        {
            var result = new EncerramentoResultDto();
            var abertos = await _queue.ListOpenOrdersForEncerramentoAsync(estabelecimentoId);
            await EncerrarAsync(estabelecimentoId, abertos.Select(c => c.Id), EncerramentoRules.MotivoManual, actorUserId, result);
            return result;
        }

        private async Task EncerrarAsync(Guid estabelecimentoId, IEnumerable<int> pedidoIds, string motivo, int? actorUserId, EncerramentoResultDto result)
        {
            foreach (var pedidoId in pedidoIds)
            {
                try
                {
                    if (await _queue.EncerrarPedidoAsync(estabelecimentoId, pedidoId, motivo, actorUserId)) result.Encerrados++;
                }
                catch (Exception ex)
                {
                    result.Falhas++;
                    _logger.LogWarning(ex, "Nao foi possivel encerrar o pedido {PedidoId} do estabelecimento {EstabelecimentoId}.", pedidoId, estabelecimentoId);
                }
            }
        }
    }

    /// <summary>Roda o encerramento a cada poucos minutos. Desligavel por configuracao (Encerramento:WorkerEnabled).</summary>
    public sealed class PedidoEncerramentoWorker : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
        private readonly IServiceScopeFactory _scopes;
        private readonly IConfiguration _configuration;
        private readonly ILogger<PedidoEncerramentoWorker> _logger;

        public PedidoEncerramentoWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<PedidoEncerramentoWorker> logger)
        {
            _scopes = scopes;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_configuration.GetValue("Encerramento:WorkerEnabled", true))
            {
                _logger.LogInformation("Encerramento automatico de pedidos desligado por configuracao (Encerramento:WorkerEnabled).");
                return;
            }

            // Espera a subida (migrations) antes da primeira passada.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
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
                    var result = await scope.ServiceProvider.GetRequiredService<EncerramentoService>().RunOnceAsync(DateTimeOffset.UtcNow);
                    if (result.Encerrados > 0 || result.Falhas > 0)
                    {
                        _logger.LogInformation("Encerramento automatico: {Encerrados} encerrado(s), {Falhas} falha(s).", result.Encerrados, result.Falhas);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Passada do encerramento automatico falhou.");
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
