using System;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Atendimento;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace APIBack.AssistenteComercial.Audio
{
    /// <summary>
    /// Fila assincrona do assistente: pega uma entrada por vez (lease no banco, varios processos nao se pisam), processa e repete.
    /// A cada hora aplica a retencao. Sem configuracao completa a entrada falha com a lista do que falta, nunca chama fornecedor.
    /// </summary>
    public sealed class AudioWorker : BackgroundService
    {
        private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
        private readonly IServiceScopeFactory _escopos;
        private readonly IOptionsMonitor<AssistenteAudioOptions> _opcoes;
        private readonly TimeProvider _relogio;
        private readonly ILogger<AudioWorker> _logger;
        private DateTime _ultimaRetencao = DateTime.MinValue;

        public AudioWorker(IServiceScopeFactory escopos, IOptionsMonitor<AssistenteAudioOptions> opcoes, TimeProvider relogio, ILogger<AudioWorker> logger)
        {
            _escopos = escopos; _opcoes = opcoes; _relogio = relogio; _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var trabalhou = false;
                try
                {
                    await using var escopo = _escopos.CreateAsyncScope();
                    var store = escopo.ServiceProvider.GetRequiredService<IAudioStore>();
                    var agora = _relogio.GetUtcNow().UtcDateTime;
                    var e = await store.ReservarProximaAsync(agora, Lease, stoppingToken);
                    if (e != null)
                    {
                        trabalhou = true;
                        await escopo.ServiceProvider.GetRequiredService<IAudioService>().ProcessarAsync(e, stoppingToken);
                    }
                    if (agora - _ultimaRetencao > TimeSpan.FromHours(1) && _opcoes.CurrentValue.RetencaoDias is > 0 and var dias)
                    {
                        _ultimaRetencao = agora;
                        var n = await store.AplicarRetencaoAsync(agora.AddDays(-dias), stoppingToken);
                        if (n > 0) _logger.LogInformation("[assistente.audio] ev=retencao apagadas={N}", n);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[assistente.audio] ev=worker_erro");
                }
                if (!trabalhou)
                    try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Ponte do pipeline do 07 (<see cref="IAssistenteInternoWhatsapp"/>). Audio de funcionario verificado vira entrada na fila; "SIM"/"NAO"
    /// responde ao ultimo resumo. Qualquer outra mensagem recebe uma resposta honesta: o assistente geral nao e este modulo.
    /// </summary>
    public sealed class AudioAssistenteInterno : IAssistenteInternoWhatsapp
    {
        private readonly IAudioService _audio;
        private readonly IRetornoWhatsapp _retorno;
        private readonly ILogger<AudioAssistenteInterno> _logger;

        public AudioAssistenteInterno(IAudioService audio, IRetornoWhatsapp retorno, ILogger<AudioAssistenteInterno> logger)
        {
            _audio = audio; _retorno = retorno; _logger = logger;
        }

        public async Task AtenderAsync(MensagemInterna m, CanalWhatsapp canal)
        {
            var ct = CancellationToken.None;
            var resposta = new EntradaAudio { PhoneNumberId = canal.PhoneNumberId, NumeroCanalE164 = canal.NumeroE164, TelefoneE164 = m.TelefoneE164, Id = Guid.Empty };
            try
            {
                if (m.Midia.Audio)
                {
                    await _audio.IngerirAsync(new AudioRecebido(m.WaMensagemId, m.Autorizacao.UsuarioId, canal.Id, m.LojaGestaoId, canal.PhoneNumberId,
                        canal.NumeroE164, m.TelefoneE164, m.Midia.MidiaId, m.Midia.Mime, m.RecebidaEmUtc), ct);
                    return;
                }
                var texto = await _audio.ResponderTextoAsync(m.Autorizacao.UsuarioId, canal.Id, m.Texto, ct)
                    ?? "Por enquanto so interpreto audios de visita (e SIM/NAO ao resumo). Envie um audio contando como foi a visita.";
                await _retorno.EnviarAsync(resposta, texto, ct);
            }
            catch (AssistenteAudioException ex)
            {
                _logger.LogWarning("[assistente.audio] ev=recusada usuario={Usuario} codigo={Codigo}", m.Autorizacao.UsuarioId, ex.Codigo);
                await _retorno.EnviarAsync(resposta, ex.Message, ct);
            }
        }
    }
}
