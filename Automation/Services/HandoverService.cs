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
        private readonly IClienteRepository _clienteRepository;

        public HandoverService(IConversationRepository repo, IAlertSender alerts, ILogger<HandoverService> logger, AgenteService agentes, IClienteRepository clienteRepository)
        {
            _repositorio = repo;
            _alertas = alerts;
            _logger = logger;
            _agentes = agentes;
            _clienteRepository = clienteRepository;
        }

        public async Task ProcessarMensagensTelegramAsync(Guid idConversa, HandoverAgentDto? agente, bool reservaConfirmada, HandoverContextDto? detalhes, long? telegramChatIdOverride = null)
        {
            if (!reservaConfirmada)
            {
                await _repositorio.DefinirModoAsync(idConversa, ModoConversa.Humano, agente?.Id);
            }

            var saudacao = !string.IsNullOrWhiteSpace(agente?.Nome)
                ? $"Olá, {agente.Nome}!"
                : "Olá, agente!";

            var destinoTelegram = telegramChatIdOverride ?? agente?.TelegramChatId;

            // Obter informações da conversa para pegar o telefone do cliente
            var conversa = await _repositorio.ObterPorIdAsync(idConversa);
            string? telefoneCliente = null;

            if (conversa != null && conversa.IdCliente != Guid.Empty && conversa.IdEstabelecimento != Guid.Empty)
            {
                telefoneCliente = await _clienteRepository.ObterTelefoneClienteAsync(conversa.IdCliente, conversa.IdEstabelecimento);
            }

            var mensagemAlerta = MontarMensagemTelegram(idConversa, reservaConfirmada, saudacao, detalhes, telefoneCliente);
            var agora = DateTime.UtcNow;

            if (AlertasRecentes.TryGetValue(idConversa, out var ultimo)
                && string.Equals(ultimo.Mensagem, mensagemAlerta, StringComparison.Ordinal)
                && (agora - ultimo.EnviadoEm) < JanelaSupressao)
            {
                Interlocked.Increment(ref _contadorSuprimidos);
                var metricas = LerMetricas();
                _logger.LogInformation("[Conversa={Conversa}] Alerta duplicado suprimido | métricas => confirmados={Confirmados}, ask={Ask}, suprimidos={Suprimidos}",
                    idConversa, metricas.confirmados, metricas.ask, metricas.suprimidos);
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
            _logger.LogInformation("[Conversa={Conversa}] {Mensagem} | métricas => confirmados={Confirmados}, ask={Ask}, suprimidos={Suprimidos}",
                idConversa, mensagemAlerta, metricasAtuais.confirmados, metricasAtuais.ask, metricasAtuais.suprimidos);

            try
            {
                await _alertas.EnviarAlertaTelegramAsync(mensagemAlerta, destinoTelegram?.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Conversa={Conversa}] Falha ao enviar alerta Telegram para handover", idConversa);
            }
        }

        private static string MontarMensagemTelegram(Guid idConversa, bool reservaConfirmada, string saudacao, HandoverContextDto? detalhes, string? telefoneCliente)
        {
            const string EmojiBullhorn = "\U0001F4E2";  // 📢
            const string EmojiPerson = "\U0001F464";    // 👤
            const string EmojiTelephone = "\U0001F4DE"; // 📞
            const string EmojiPeople = "\U0001F465";    // 👥
            const string EmojiCalendar = "\U0001F4C5";  // 📅
            const string EmojiPin = "\U0001F4CD";       // 📍

            var builder = new StringBuilder();

            if (reservaConfirmada)
            {
                builder.AppendLine($"{EmojiBullhorn} Nova reserva confirmada!");
                builder.AppendLine();
                builder.AppendLine($"{EmojiPerson} Nome: {TextoOuNaoInformado(detalhes?.ClienteNome)}");
                builder.AppendLine($"{EmojiTelephone} Telefone: {TextoOuNaoInformado(telefoneCliente)}");
                builder.AppendLine($"{EmojiPeople} Pessoas: {TextoOuNaoInformado(detalhes?.NumeroPessoas)}");
                builder.AppendLine($"{EmojiCalendar} Data: {MontarDescricaoData(detalhes)}");
                builder.AppendLine();
                builder.AppendLine($"{EmojiPin} Mais detalhes: zippygo.com");
            }
            else
            {
                builder.AppendLine(saudacao);
                builder.AppendLine();
                builder.AppendLine("Cliente pediu atendimento.");
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
                return $"{detalhes!.Dia!.Trim()} às {detalhes.Horario!.Trim()}";
            }

            if (possuiDia)
            {
                return detalhes!.Dia!.Trim();
            }

            if (possuiHorario)
            {
                return detalhes!.Horario!.Trim();
            }

            return "Não informado";
        }

        private static string TextoOuNaoInformado(string? valor)
            => string.IsNullOrWhiteSpace(valor) ? "Não informado" : valor.Trim();

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

            await ProcessarMensagensTelegramAsync(idConversa, agente, reservaConfirmada, detalhes, chatId);
        }

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
