// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class ConversationProcessor
    {
        private readonly ConversationService _conversationService;
        private readonly IQueueBus _fila;
        private readonly IWabaPhoneRepository _wabaRepo;
        private readonly IIARegraRepository _regrasRepo;
        private readonly IMessageRepository _mensagemRepository;
        private readonly ILogger<ConversationProcessor> _logger;

        public ConversationProcessor(
            ConversationService conversationService,
            IQueueBus fila,
            IWabaPhoneRepository wabaRepo,
            IIARegraRepository regrasRepo,
            IMessageRepository mensagemRepository,
            ILogger<ConversationProcessor> logger)
        {
            _conversationService = conversationService;
            _fila = fila;
            _wabaRepo = wabaRepo;
            _regrasRepo = regrasRepo;
            _mensagemRepository = mensagemRepository;
            _logger = logger;
        }

        public async Task<ConversationProcessingResult> ProcessAsync(ConversationProcessingInput input)
        {
            if (MensagemDoSistema(input))
            {
                _logger.LogInformation("[Webhook] Ignorando mensagem automática do sistema (from={From})", input.Mensagem.De);
                return new ConversationProcessingResult(true, null, null, Array.Empty<AssistantChatTurn>(), null, new HandoverContextDto(), input.Texto, input.PhoneNumberDisplay, input.PhoneNumberId);
            }

            var criada = await _conversationService.AcrescentarEntradaAsync(
                idWa: input.Mensagem.De!,
                idMensagemWa: input.Mensagem.Id!,
                conteudo: input.Texto,
                phoneNumberId: input.PhoneNumberDisplay ?? string.Empty,
                dataMensagemUtc: input.DataMensagemUtc,
                tipoOrigem: input.Mensagem.Tipo);

            if (criada == null)
            {
                _logger.LogInformation("[Webhook] Entrada ignorada após verificação de duplicidade. From={From}", input.Mensagem.De);
                return new ConversationProcessingResult(true, null, null, Array.Empty<AssistantChatTurn>(), null, new HandoverContextDto(), input.Texto, input.PhoneNumberDisplay, input.PhoneNumberId);
            }

            criada.CriadaPor ??= "cliente";
            await _fila.PublicarEntradaAsync(criada);

            var contexto = await ObterContextoAsync(criada.IdConversa, input.PhoneNumberDisplay);
            var historico = await ObterHistoricoAsync(criada.IdConversa);
            var handoverDetalhes = MontarHandoverDetalhes(input, criada, historico, contexto);

            return new ConversationProcessingResult(
                ShouldIgnore: false,
                MensagemRegistrada: criada,
                IdConversa: criada.IdConversa,
                Historico: historico,
                Contexto: contexto,
                HandoverDetalhes: handoverDetalhes,
                TextoUsuario: input.Texto,
                NumeroTelefoneExibicao: input.PhoneNumberDisplay,
                NumeroWhatsappId: input.PhoneNumberId);
        }

                        private bool MensagemDoSistema(ConversationProcessingInput input)
        {
            if (input.Mensagem == null) return true;
            if (string.IsNullOrWhiteSpace(input.Mensagem.De)) return true;

            var from = SanitizarNumero(input.Mensagem.De);
            var display = SanitizarNumero(input.PhoneNumberDisplay);
            var phoneId = SanitizarNumero(input.PhoneNumberId);

            if (!string.IsNullOrEmpty(display) && string.Equals(from, display, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(phoneId) && string.Equals(from, phoneId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static string SanitizarNumero(string? valor)
        {
            if (string.IsNullOrWhiteSpace(valor)) return string.Empty;
            var span = valor.AsSpan();
            var builder = new StringBuilder(span.Length);
            foreach (var ch in span)
            {
                if (char.IsDigit(ch) || ch == '+')
                {
                    builder.Append(ch);
                }
            }
            return builder.ToString();
        }

        private async Task<string?> ObterContextoAsync(Guid idConversa, string? phoneNumberId)
        {
            try
            {
                Guid? idEstab = null;
                if (!string.IsNullOrWhiteSpace(phoneNumberId))
                {
                    idEstab = await _wabaRepo.ObterIdEstabelecimentoPorPhoneNumberIdAsync(phoneNumberId);
                }

                if (!idEstab.HasValue || idEstab == Guid.Empty)
                {
                    return null;
                }

                var regras = await _regrasRepo.ListaregrasAsync(idEstab.Value);
                var ativas = regras?
                    .Where(r => r.Ativo && !string.IsNullOrWhiteSpace(r.Contexto))
                    .OrderByDescending(r => r.DataAtualizacao)
                    .ThenByDescending(r => r.DataCriacao)
                    .ToList();

                if (ativas != null && ativas.Count > 0)
                {
                    return string.Join("\n\n", ativas.Select(r => r.Contexto));
                }

                return await _regrasRepo.ObterContextoAtivoAsync(idEstab.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao carregar contexto para conversa {Conversa}", idConversa);
                return null;
            }
        }

        private async Task<IReadOnlyList<AssistantChatTurn>> ObterHistoricoAsync(Guid idConversa)
        {
            try
            {
                var historico = await _mensagemRepository.GetByConversationAsync(idConversa, limit: 200);
                return historico
                    .Where(m => !string.IsNullOrWhiteSpace(m.Conteudo))
                    .Select(m => new AssistantChatTurn
                    {
                        Role = m.Direcao == DirecaoMensagem.Entrada ? "user" : "assistant",
                        Content = m.Conteudo,
                        Timestamp = m.DataHora
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao carregar histórico para conversa {Conversa}", idConversa);
                return Array.Empty<AssistantChatTurn>();
            }
        }

        private static HandoverContextDto MontarHandoverDetalhes(ConversationProcessingInput input, Message mensagem, IReadOnlyList<AssistantChatTurn> historico, string? contexto)
        {
            var clienteNome = input.Valor.Contatos?.FirstOrDefault()?.Perfil?.Nome;
            return new HandoverContextDto
            {
                ClienteNome = string.IsNullOrWhiteSpace(clienteNome) ? mensagem.IdConversa.ToString() : clienteNome,
                Telefone = SanitizarNumero(input.Mensagem.De),
                Motivo = null,
                NumeroPessoas = null,
                Dia = null,
                Horario = null,
                Contexto = contexto,
                Historico = historico.Select(turno => $"{(turno.Role == "assistant" ? "Assistente" : "Cliente")}: {turno.Content}").ToList()
            };
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

