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
                builder.AppendLine("✅ Reserva confirmada! Seguem os detalhes:");
                builder.AppendLine($"• 👤 Cliente: {ValorOuNaoInformado(detalhes?.ClienteNome)}");
                builder.AppendLine($"• 📅 Dia: {ValorOuNaoInformado(detalhes?.Dia)}");
                builder.AppendLine($"• ⏰ Horário: {ValorOuNaoInformado(detalhes?.Horario)}");
                builder.AppendLine($"• ☎️ Telefone: {ValorOuNaoInformado(detalhes?.Telefone)}");
                builder.AppendLine($"• 🆔 Conversa: {idConversa}");
            }
            else
            {
                builder.AppendLine("⚠️ O cliente solicitou atendimento humano. Veja o resumo:");
                builder.AppendLine($"• 🆔 Conversa: {idConversa}");
                builder.AppendLine($"• 👤 Cliente: {ValorOuNaoInformado(detalhes?.ClienteNome)}");

                var queixa = string.IsNullOrWhiteSpace(detalhes?.QueixaPrincipal)
                    ? "Cliente não quis informar"
                    : detalhes!.QueixaPrincipal!.Trim();
                builder.AppendLine($"• 📌 Queixa: {queixa}");

                if (!string.IsNullOrWhiteSpace(detalhes?.Contexto))
                {
                    builder.AppendLine("• 🧠 Contexto:");
                    builder.AppendLine($"  {detalhes.Contexto.Trim()}");
                }

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

            static string ValorOuNaoInformado(string? valor)
                => string.IsNullOrWhiteSpace(valor) ? "Não informado" : valor.Trim();
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
