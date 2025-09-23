// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class HandoverService
    {
        private static readonly TimeSpan JanelaSupressao = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan JanelaLimpeza = TimeSpan.FromMinutes(5);
        private static readonly ConcurrentDictionary<Guid, AlertaRecente> AlertasRecentes = new();
        private static DateTime _ultimaLimpeza = DateTime.UtcNow;
        private static long _contadorConfirmados;
        private static long _contadorAsk;
        private static long _contadorSuprimidos;

        private static (long confirmados, long ask, long suprimidos) LerMetricas()
        {
            return (
                confirmados: Interlocked.Read(ref _contadorConfirmados),
                ask: Interlocked.Read(ref _contadorAsk),
                suprimidos: Interlocked.Read(ref _contadorSuprimidos));
        }

        private readonly record struct AlertaRecente(string Mensagem, DateTime EnviadoEm);

        private readonly IConversationRepository _repositorio;
        private readonly IAlertSender _alertas;
        private readonly AgenteService _agentes;
        private readonly ILogger<HandoverService> _logger;

        public HandoverService(IConversationRepository repo, IAlertSender alerts, ILogger<HandoverService> logger, AgenteService agentes)
        {
            _repositorio = repo;
            _alertas = alerts;
            _logger = logger;
            _agentes = agentes;
        }

        public async Task ProcessarHandoverAsync(Guid idConversa, HandoverAgentDto? agente, bool reservaConfirmada, HandoverContextDto? detalhes, long? telegramChatIdOverride = null)
        {
            await _repositorio.DefinirModoAsync(idConversa, ModoConversa.Humano, agente?.Id);

            var saudacao = !string.IsNullOrWhiteSpace(agente?.Nome)
                ? $"Olá¡, {agente.Nome}!"
                : "Olá¡, agente!";

            var destinoTelegram = telegramChatIdOverride ?? agente?.TelegramChatId;
            var mensagemAlerta = MontarMensagemTelegram(idConversa, reservaConfirmada, saudacao, detalhes);
            var agora = DateTime.UtcNow;

            if (AlertasRecentes.TryGetValue(idConversa, out var ultimo)
                && string.Equals(ultimo.Mensagem, mensagemAlerta, StringComparison.Ordinal)
                && (agora - ultimo.EnviadoEm) < JanelaSupressao)
            {
                Interlocked.Increment(ref _contadorSuprimidos);
                var metricas = LerMetricas();
                _logger.LogInformation("[Conversa={Conversa}] Alerta duplicado suprimido | mÃ©tricas => confirmados={Confirmados}, ask={Ask}, suprimidos={Suprimidos}", idConversa, metricas.confirmados, metricas.ask, metricas.suprimidos);
                return;
            }

            AlertasRecentes[idConversa] = new AlertaRecente(mensagemAlerta, agora);
            LimparAlertasAntigos(agora);

            if (reservaConfirmada)
            {
                Interlocked.Increment(ref _contadorConfirmados);
            }
            else
            {
                Interlocked.Increment(ref _contadorAsk);
            }

            var metricasAtuais = LerMetricas();
            _logger.LogInformation("[Conversa={Conversa}] {Mensagem} | mÃ©tricas => confirmados={Confirmados}, ask={Ask}, suprimidos={Suprimidos}", idConversa, mensagemAlerta, metricasAtuais.confirmados, metricasAtuais.ask, metricasAtuais.suprimidos);

            try
            {
                await _alertas.EnviarAlertaAsync(mensagemAlerta, destinoTelegram?.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Conversa={Conversa}] Falha ao enviar alerta Telegram para handover", idConversa);
            }
        }

        public async Task DefinirHumanoAsync(Guid idConversa, HandoverAgentDto? agente, bool reservaConfirmada = false, HandoverContextDto? detalhes = null)
        {
            long? chatId = agente?.TelegramChatId;
            if (!chatId.HasValue && agente?.Id > 0)
            {
                try
                {
                    chatId = await _agentes.ObterTelegramChatIdAsync(agente.Id);
                }
                catch
                {
                }
            }

            await ProcessarHandoverAsync(idConversa, agente, reservaConfirmada, detalhes, chatId);
        }

        private static string MontarMensagemTelegram(Guid idConversa, bool reservaConfirmada, string saudacao, HandoverContextDto? detalhes)
        {
            var builder = new StringBuilder();

            if (reservaConfirmada)
            {
                builder.AppendLine("📢 Nova reserva confirmada!");
                builder.AppendLine();
                builder.AppendLine($"👤 Nome: {TextoOuNaoInformado(detalhes?.ClienteNome)}");
                builder.AppendLine($"📞 Telefone: {TextoOuNaoInformado(detalhes?.Telefone)}");
                builder.AppendLine($"👥 Pessoas: {TextoOuNaoInformado(detalhes?.NumeroPessoas)}");
                builder.AppendLine($"📅 Data: {MontarDescricaoData(detalhes)}");
                builder.AppendLine();
                builder.AppendLine("🔗 Mais detalhes: zippygo.com");
            }
            else
            {
                builder.AppendLine(saudacao);
                builder.AppendLine();
                builder.AppendLine("Cliente pediu atendimento humano.");
                builder.AppendLine();
                builder.AppendLine("Histórico da conversa:");

                var historico = detalhes?.Historico?
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Take(10)
                    .ToList();

                if (historico != null && historico.Count > 0)
                {
                    foreach (var item in historico)
                    {
                        builder.AppendLine(item.Trim());
                    }
                }
                else
                {
                    builder.AppendLine("(Histórico indisponível)");
                }
            }

            builder.AppendLine();
            return builder.ToString().TrimEnd();
        }

private static string MontarDescricaoData(HandoverContextDto? detalhes)
        {
            var possuiDia = !string.IsNullOrWhiteSpace(detalhes?.Dia);
            var possuiHorario = !string.IsNullOrWhiteSpace(detalhes?.Horario);

            if (possuiDia && possuiHorario)
            {
                return $"{detalhes!.Dia!.Trim()} Ã s {detalhes.Horario!.Trim()}";
            }

            if (possuiDia)
            {
                return detalhes!.Dia!.Trim();
            }

            if (possuiHorario)
            {
                return detalhes!.Horario!.Trim();
            }

            return "NÃ£o informado";
        }

        private static string TextoOuNaoInformado(string? valor)
            => string.IsNullOrWhiteSpace(valor) ? "NÃ£o informado" : valor.Trim();

        private static void LimparAlertasAntigos(DateTime agora)
        {
            if ((agora - _ultimaLimpeza) < JanelaLimpeza)
            {
                return;
            }

            foreach (var par in AlertasRecentes.ToArray())
            {
                if ((agora - par.Value.EnviadoEm) > JanelaLimpeza)
                {
                    AlertasRecentes.TryRemove(par.Key, out _);
                }
            }

            _ultimaLimpeza = agora;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================


