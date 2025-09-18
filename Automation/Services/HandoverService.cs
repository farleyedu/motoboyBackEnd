// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class HandoverService
    {
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

        // Novo fluxo principal: notifica no Telegram e ativa modo humano, atualizando estado para 'agente'
        public async Task ProcessarHandoverAsync(Guid idConversa, HandoverAgentDto? agente, bool reservaConfirmada, HandoverContextDto? detalhes, long? telegramChatIdOverride = null)
        {
            await _repositorio.DefinirModoAsync(idConversa, ModoConversa.Humano, agente?.Id);

            var saudacao = !string.IsNullOrWhiteSpace(agente?.Nome)
                ? $"Olá, {agente.Nome}!"
                : "Olá, agente!";

            var destinoTelegram = telegramChatIdOverride ?? agente?.TelegramChatId;
            var mensagemAlerta = MontarMensagemTelegram(idConversa, saudacao, reservaConfirmada, detalhes);

            _logger.LogInformation(mensagemAlerta);

            try
            {
                await _alertas.EnviarAlertaAsync(mensagemAlerta, destinoTelegram?.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao enviar alerta Telegram para handover (conversa {Conversa})", idConversa);
            }
        }

        // Mantém o método anterior por compatibilidade; delega para o novo com reservaConfirmada=false
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

        private static string MontarMensagemTelegram(Guid idConversa, string saudacao, bool reservaConfirmada, HandoverContextDto? detalhes)
        {
            var builder = new StringBuilder();
            builder.AppendLine(saudacao);
            builder.AppendLine();

            if (reservaConfirmada)
            {
                builder.AppendLine("✅ Formulário de reserva recebido:");

                void AppendLinha(string titulo, string? valor)
                {
                    if (!string.IsNullOrWhiteSpace(valor))
                    {
                        builder.AppendLine($"{titulo}: {valor.Trim()}");
                    }
                }

                AppendLinha("Nome", detalhes?.ClienteNome);
                AppendLinha("Número de pessoas", detalhes?.NumeroPessoas);
                AppendLinha("Dia", detalhes?.Dia);
                AppendLinha("Horário", detalhes?.Horario);
                AppendLinha("Contato", detalhes?.Telefone);
                builder.AppendLine($"Conversa: {idConversa}");
            }
            else
            {
                builder.AppendLine("⚠️ O cliente solicitou atendimento humano. Veja o resumo:");
                builder.AppendLine($"• 🆔 Conversa: {idConversa}");

                if (!string.IsNullOrWhiteSpace(detalhes?.ClienteNome))
                {
                    builder.AppendLine($"• 👤 Cliente: {detalhes.ClienteNome.Trim()}");
                }

                if (!string.IsNullOrWhiteSpace(detalhes?.Telefone))
                {
                    builder.AppendLine($"• ☎️ Contato: {detalhes.Telefone.Trim()}");
                }

                var motivo = string.IsNullOrWhiteSpace(detalhes?.Motivo)
                    ? (string.IsNullOrWhiteSpace(detalhes?.QueixaPrincipal) ? "Solicitação do cliente." : detalhes!.QueixaPrincipal!.Trim())
                    : detalhes.Motivo.Trim();
                builder.AppendLine($"• 📌 Motivo: {motivo}");

                var historico = detalhes?.Historico?
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Take(10)
                    .ToList();

                if (historico != null && historico.Count > 0)
                {
                    builder.AppendLine("• 📜 Histórico:");
                    foreach (var item in historico)
                    {
                        builder.AppendLine($"  - {item.Trim()}");
                    }
                }
            }

            return builder.ToString().TrimEnd();
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
