using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using APIBack.DTOs.Rastreio;
using APIBack.Repository.Interface;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace APIBack.Service
{
    /// <summary>Envia o texto do aviso pela conversa do cliente (implementado pelo modulo de conversas).</summary>
    public interface ITrackingNoticeSender
    {
        /// <summary>Devolve o id da mensagem gravada na conversa; lanca se nao foi possivel enviar.</summary>
        Task<Guid> SendAsync(Guid conversaId, Guid estabelecimentoId, string text);
    }

    /// <summary>
    /// Avisos ao cliente (Fase 6): "saiu da loja" e "motoboy chegando", so para pedido com opt-in, um disparo por tipo
    /// e pedido (UNIQUE no banco: quem reserva primeiro envia) e sem reenvio em loop (falha fica registrada).
    /// </summary>
    public sealed class TrackingNoticeService
    {
        private readonly IRastreioRepository _rastreio;
        private readonly IAtendimentoRepository _atendimento;
        private readonly ITrackingNoticeSender _sender;
        private readonly IConfiguration _configuration;
        private readonly ILogger<TrackingNoticeService> _logger;

        public TrackingNoticeService(
            IRastreioRepository rastreio,
            IAtendimentoRepository atendimento,
            ITrackingNoticeSender sender,
            IConfiguration configuration,
            ILogger<TrackingNoticeService> logger)
        {
            _rastreio = rastreio;
            _atendimento = atendimento;
            _sender = sender;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>Uma passada: olha os pedidos em rota com opt-in e dispara o que estiver na hora. Devolve quantos avisos tentou enviar.</summary>
        public async Task<int> RunOnceAsync(ArrivingHysteresis hysteresis, DateTimeOffset now)
        {
            var candidates = await _rastreio.GetCandidatesAsync();
            var attempted = 0;
            foreach (var candidate in candidates)
            {
                try
                {
                    if (!candidate.DispatchDone)
                    {
                        if (candidate.Settings.DispatchEnabled)
                        {
                            attempted += await SendAsync(candidate, NoticeTypes.Dispatch, null);
                        }
                        else
                        {
                            await RecordIgnoredAsync(candidate.PedidoId, NoticeTypes.Dispatch, "aviso_desligado");
                        }
                    }

                    if (!candidate.ArrivingDone)
                    {
                        var (verdict, _) = ArrivingRules.Evaluate(new ArrivingInput(
                            candidate.MotoboyShares, candidate.DistanceMeters, candidate.LocationAgeSeconds, candidate.SpeedMps, candidate.Settings));
                        var ready = hysteresis.Observe(candidate.PedidoId, verdict == ArrivingVerdict.Qualifies, now);
                        if (ready)
                        {
                            var minutes = candidate.DistanceMeters.HasValue
                                ? Math.Max(1, (int)Math.Ceiling(TrackingEta.Minutes(candidate.DistanceMeters.Value, candidate.SpeedMps)))
                                : candidate.Settings.ArrivingMinutes;
                            attempted += await SendAsync(candidate, NoticeTypes.Arriving, minutes);
                            hysteresis.Forget(candidate.PedidoId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Aviso de rastreio do pedido {Pedido} falhou nesta passada.", candidate.PedidoId);
                }
            }
            return attempted;
        }

        private async Task RecordIgnoredAsync(int pedidoId, string type, string reason)
        {
            var id = await _rastreio.TryReserveAsync(pedidoId, type);
            if (id.HasValue) await _rastreio.MarkAsync(id.Value, NoticeStatuses.Ignored, reason, null);
        }

        private async Task<int> SendAsync(NoticeCandidate candidate, string type, int? minutes)
        {
            var reserved = await _rastreio.TryReserveAsync(candidate.PedidoId, type);
            if (!reserved.HasValue) return 0; // outro processo ja cuida deste aviso

            try
            {
                var conversa = await _atendimento.GetConversaDoPedidoAsync(candidate.EstabelecimentoId, candidate.PedidoId);
                if (conversa.ConversaId == null)
                {
                    await _rastreio.MarkAsync(reserved.Value, NoticeStatuses.Ignored, "sem_conversa", null);
                    return 0;
                }
                if (!conversa.JanelaAberta)
                {
                    // Fora da janela de 24h so vale template aprovado, que ainda nao existe: falha visivel, sem reenvio.
                    await _rastreio.MarkAsync(reserved.Value, NoticeStatuses.Failed, "fora_da_janela_sem_template", null);
                    return 0;
                }

                var baseUrl = _configuration["Rastreio:PublicBaseUrl"]?.Trim().TrimEnd('/');
                string? link = null;
                // Valor de marcador ("__SET_IN_ENV__") ou sem http(s) conta como nao configurado.
                if (!string.IsNullOrEmpty(baseUrl) && baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    link = $"{baseUrl}/rastreio/{await _rastreio.EnsureTokenAsync(candidate.PedidoId)}";
                }

                var template = type == NoticeTypes.Dispatch ? candidate.Settings.DispatchText : candidate.Settings.ArrivingText;
                var rendered = QuickReplyRenderer.Render(template, BuildValues(candidate, minutes, link));
                if (rendered.Pendentes.Count > 0)
                {
                    await _rastreio.MarkAsync(reserved.Value, NoticeStatuses.Failed, "variavel_sem_valor:" + string.Join(",", rendered.Pendentes), null);
                    return 0;
                }

                var messageId = await _sender.SendAsync(conversa.ConversaId.Value, candidate.EstabelecimentoId, rendered.Texto);
                await _rastreio.MarkAsync(reserved.Value, NoticeStatuses.Sent, null, messageId);
                return 1;
            }
            catch (Exception ex)
            {
                await _rastreio.MarkAsync(reserved.Value, NoticeStatuses.Failed, "envio: " + ex.Message, null);
                return 0;
            }
        }

        internal static IReadOnlyDictionary<string, string?> BuildValues(NoticeCandidate candidate, int? minutes, string? link) =>
            new Dictionary<string, string?>
            {
                ["cliente"] = FirstName(candidate.NomeCliente) ?? "cliente",
                ["numero"] = candidate.PedidoId.ToString(),
                ["loja"] = candidate.Loja,
                ["motoboy"] = FirstName(candidate.MotoboyNome),
                ["minutos"] = minutes?.ToString(),
                ["link"] = link,
            };

        internal static string? FirstName(string? name)
        {
            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            var space = trimmed.IndexOf(' ');
            return space > 0 ? trimmed[..space] : trimmed;
        }

        // ---- link publico ------------------------------------------------------

        public async Task<PublicTrackingView> GetPublicViewAsync(string token)
        {
            if (!RastreioToken.IsWellFormed(token))
            {
                throw new DeliveryDomainException(404, "TRACKING_NOT_FOUND", "Link de rastreio nao encontrado.");
            }
            var source = await _rastreio.GetPublicSourceAsync(token)
                ?? throw new DeliveryDomainException(404, "TRACKING_NOT_FOUND", "Link de rastreio nao encontrado.");
            var (status, label, final) = PublicTrackingRules.StatusOf(source.StatusPedido);

            if (source.Expired && !final)
            {
                throw new DeliveryDomainException(410, "TRACKING_LINK_EXPIRED", "Este link de rastreio expirou.");
            }

            var view = new PublicTrackingView
            {
                Status = status,
                StatusLabel = label,
                Finalizado = final,
                PedidoNumero = source.PedidoId,
                Loja = source.Loja
            };
            if (final)
            {
                // Entrega finalizada: o link deixa de valer e nao mostra mais nada alem do estado.
                await _rastreio.InvalidateTokenAsync(source.PedidoId);
                return view;
            }

            view.PrevisaoEntrega = source.Previsao?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            view.MotoboyNome = FirstName(source.MotoboyNome);
            view.Destino = PublicTrackingRules.Round(source.DestLat, source.DestLon);
            view.LojaPosicao = PublicTrackingRules.Round(source.LojaLat, source.LojaLon);
            view.PosicaoMotoboy = PublicTrackingRules.MotoboyPosition(
                source.MotoboyShares, source.StatusPedido, source.MotoLat, source.MotoLon, source.LocationAgeSeconds);
            return view;
        }
    }

    /// <summary>Roda o servico de avisos a cada poucos segundos. Desligavel por configuracao (Rastreio:WorkerEnabled).</summary>
    public sealed class TrackingNoticeWorker : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
        private readonly IServiceScopeFactory _scopes;
        private readonly IConfiguration _configuration;
        private readonly ILogger<TrackingNoticeWorker> _logger;
        private readonly ArrivingHysteresis _hysteresis = new();

        public TrackingNoticeWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<TrackingNoticeWorker> logger)
        {
            _scopes = scopes;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_configuration.GetValue("Rastreio:WorkerEnabled", true))
            {
                _logger.LogInformation("Avisos de rastreio desligados por configuracao (Rastreio:WorkerEnabled).");
                return;
            }

            // Espera a subida (migrations) antes da primeira passada.
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<TrackingNoticeService>().RunOnceAsync(_hysteresis, DateTimeOffset.UtcNow);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Passada de avisos de rastreio falhou.");
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
