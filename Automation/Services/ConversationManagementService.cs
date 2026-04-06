using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Helpers;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class ConversationManagementService
    {
        private const string WhatsAppWindowExpiredCode = "whatsapp_window_expired";
        private const string WhatsAppWindowExpiredMessage = "A janela de 24 horas do WhatsApp expirou. A Meta nao permite iniciar uma nova conversa por aqui apos esse prazo.";

        private static readonly HashSet<string> StatusAbertos = new(StringComparer.OrdinalIgnoreCase)
        {
            "com_bot",
            "em_andamento",
            "aguardando_cliente",
            "aguardando_interno"
        };

        private static readonly HashSet<string> StatusHumanos = new(StringComparer.OrdinalIgnoreCase)
        {
            "em_andamento",
            "aguardando_cliente",
            "aguardando_interno"
        };

        private static readonly HashSet<string> StatusAlteraveis = new(StringComparer.OrdinalIgnoreCase)
        {
            "com_bot",
            "em_andamento",
            "aguardando_cliente",
            "aguardando_interno"
        };

        private static readonly HashSet<string> TiposFechamento = new(StringComparer.OrdinalIgnoreCase)
        {
            "manual",
            "inatividade"
        };

        private readonly IConversationRepository _conversationRepository;
        private readonly IMessageService _messageService;
        private readonly IClienteRepository _clienteRepository;
        private readonly IWabaPhoneRepository _wabaPhoneRepository;
        private readonly IQueueBus _queueBus;
        private readonly IServicoAtendimentoRepository _servicoAtendimentoRepository;
        private readonly WhatsAppSender _whatsAppSender;
        private readonly AgenteService _agenteService;
        private readonly ConversationResetService _conversationReset;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ConversationManagementService> _logger;

        public ConversationManagementService(
            IConversationRepository conversationRepository,
            IMessageService messageService,
            IClienteRepository clienteRepository,
            IWabaPhoneRepository wabaPhoneRepository,
            IQueueBus queueBus,
            IServicoAtendimentoRepository servicoAtendimentoRepository,
            WhatsAppSender whatsAppSender,
            AgenteService agenteService,
            ConversationResetService conversationReset,
            IConfiguration configuration,
            ILogger<ConversationManagementService> logger)
        {
            _conversationRepository = conversationRepository;
            _messageService = messageService;
            _clienteRepository = clienteRepository;
            _wabaPhoneRepository = wabaPhoneRepository;
            _queueBus = queueBus;
            _servicoAtendimentoRepository = servicoAtendimentoRepository;
            _whatsAppSender = whatsAppSender;
            _agenteService = agenteService;
            _conversationReset = conversationReset;
            _configuration = configuration;
            _logger = logger;
        }

        public Task<IReadOnlyList<ConversationAgentDto>> ListarAgentesAsync(Guid idEstabelecimento)
        {
            return _conversationRepository.ListarAgentesAsync(idEstabelecimento);
        }

        public async Task<ConversationActionResponseDto> AssignAsync(
            Guid requestedConversationId,
            Guid idEstabelecimento,
            int? actorUserId,
            string? actorName,
            AssignConversationRequest? request)
        {
            var controleAtual = await EnsureControlAsync(requestedConversationId, idEstabelecimento);
            EnsureNotClosed(controleAtual);

            var agente = await ResolveAgentAsync(
                request?.IdAgente > 0 ? request.IdAgente : null,
                actorUserId,
                request?.NomeAgente);

            if (agente == null)
            {
                throw new ConversationManagementException(422, "Nao foi possivel resolver o agente para assumir a conversa.");
            }

            var updated = await _conversationRepository.AtribuirConversaAsync(requestedConversationId, agente.Id, idEstabelecimento);
            if (!updated)
            {
                throw new ConversationManagementException(404, "Conversa nao encontrada.");
            }

            await RegistrarEventoAsync(
                controleAtual.ConversationId,
                idEstabelecimento,
                "assigned",
                actorName ?? agente.Nome,
                controleAtual.Status,
                "em_andamento",
                null,
                null,
                "api",
                actorUserId,
                agente.Id,
                new Dictionary<string, object?>
                {
                    ["assignedAgentId"] = agente.Id,
                    ["assignedAgentName"] = agente.Nome
                });

            await SyncServicoAtendimentoStatusAsync(requestedConversationId, "em_andamento");

            return await BuildResponseAsync(requestedConversationId, idEstabelecimento);
        }

        public async Task<ConversationActionResponseDto> BackToBotAsync(
            Guid requestedConversationId,
            Guid idEstabelecimento,
            int? actorUserId,
            string? actorName)
        {
            var controleAtual = await EnsureControlAsync(requestedConversationId, idEstabelecimento);
            EnsureNotClosed(controleAtual);

            var updated = await _conversationRepository.VoltarParaBotAsync(requestedConversationId, idEstabelecimento);
            if (!updated)
            {
                throw new ConversationManagementException(404, "Conversa nao encontrada.");
            }

            await RegistrarEventoAsync(
                controleAtual.ConversationId,
                idEstabelecimento,
                "returned_to_bot",
                actorName,
                controleAtual.Status,
                "com_bot",
                null,
                null,
                "api",
                actorUserId,
                null);

            await SyncServicoAtendimentoStatusAsync(requestedConversationId, "com_bot");

            return await BuildResponseAsync(requestedConversationId, idEstabelecimento);
        }

        public async Task<ConversationActionResponseDto> UpdateStatusAsync(
            Guid requestedConversationId,
            Guid idEstabelecimento,
            int? actorUserId,
            string? actorName,
            UpdateConversationStatusRequest? request)
        {
            var status = NormalizeStatus(request?.Status);
            if (string.IsNullOrWhiteSpace(status) || !StatusAlteraveis.Contains(status))
            {
                throw new ConversationManagementException(422, "Status de atendimento invalido.");
            }

            var controleAtual = await EnsureControlAsync(requestedConversationId, idEstabelecimento);
            EnsureNotClosed(controleAtual);

            if (string.Equals(controleAtual.Status, status, StringComparison.OrdinalIgnoreCase))
            {
                return await BuildResponseAsync(requestedConversationId, idEstabelecimento);
            }

            if (string.Equals(status, "com_bot", StringComparison.OrdinalIgnoreCase))
            {
                var updatedBot = await _conversationRepository.VoltarParaBotAsync(requestedConversationId, idEstabelecimento);
                if (!updatedBot)
                {
                    throw new ConversationManagementException(404, "Conversa nao encontrada.");
                }
            }
            else
            {
                var agenteId = controleAtual.AssignedAgentId;
                if (!agenteId.HasValue)
                {
                    var agente = await ResolveAgentAsync(null, actorUserId, null);
                    agenteId = agente?.Id;
                }

                if (!agenteId.HasValue)
                {
                    throw new ConversationManagementException(422, "Status humano exige um agente atribuido.");
                }

                var updated = await _conversationRepository.AtualizarStatusAtendimentoAsync(
                    requestedConversationId,
                    status,
                    agenteId,
                    actorName,
                    idEstabelecimento);

                if (!updated)
                {
                    throw new ConversationManagementException(404, "Conversa nao encontrada.");
                }
            }

            await RegistrarEventoAsync(
                controleAtual.ConversationId,
                idEstabelecimento,
                "status_changed",
                actorName,
                controleAtual.Status,
                status,
                null,
                null,
                "api",
                actorUserId,
                null);

            await SyncServicoAtendimentoStatusAsync(requestedConversationId, status);

            return await BuildResponseAsync(requestedConversationId, idEstabelecimento);
        }

        public async Task<ConversationActionResponseDto> CloseAsync(
            Guid requestedConversationId,
            Guid idEstabelecimento,
            int? actorUserId,
            string? actorName,
            CloseConversationRequest? request)
        {
            var tipoFechamento = NormalizeCloseType(request?.Tipo);
            if (!TiposFechamento.Contains(tipoFechamento))
            {
                throw new ConversationManagementException(422, "Tipo de fechamento invalido.");
            }

            var controleAtual = await EnsureControlAsync(requestedConversationId, idEstabelecimento);
            EnsureNotClosed(controleAtual);

            int? agenteId = request?.IdAgente > 0 ? request.IdAgente : controleAtual.AssignedAgentId;
            if (!agenteId.HasValue && string.Equals(tipoFechamento, "manual", StringComparison.OrdinalIgnoreCase))
            {
                agenteId = (await ResolveAgentAsync(null, actorUserId, null))?.Id;
            }

            var updated = await _conversationRepository.FecharConversaAsync(
                requestedConversationId,
                agenteId,
                request?.Motivo,
                idEstabelecimento,
                tipoFechamento);

            if (!updated)
            {
                throw new ConversationManagementException(404, "Conversa nao encontrada.");
            }

            var novoStatus = string.Equals(tipoFechamento, "inatividade", StringComparison.OrdinalIgnoreCase)
                ? "encerrada_inatividade"
                : "encerrada_manual";

            await RegistrarEventoAsync(
                controleAtual.ConversationId,
                idEstabelecimento,
                "closed",
                actorName,
                controleAtual.Status,
                novoStatus,
                request?.Motivo,
                tipoFechamento,
                "api",
                actorUserId,
                agenteId);

            await SyncServicoAtendimentoStatusAsync(requestedConversationId, novoStatus, tipoFechamento);

            if (string.Equals(tipoFechamento, "manual", StringComparison.OrdinalIgnoreCase))
            {
                await _conversationReset.ResetAfterManualCloseAsync(requestedConversationId);
            }

            return await BuildResponseAsync(requestedConversationId, idEstabelecimento);
        }

        public async Task<ConversationActionResponseDto> ReopenAsync(
            Guid requestedConversationId,
            Guid idEstabelecimento,
            int? actorUserId,
            string? actorName,
            ReopenConversationRequest? request)
        {
            var controleAtual = await EnsureControlAsync(requestedConversationId, idEstabelecimento);
            if (StatusAbertos.Contains(controleAtual.Status))
            {
                throw new ConversationManagementException(409, "Conversa ja esta aberta.");
            }

            if (string.Equals(controleAtual.SendBlockReasonCode, WhatsAppWindowExpiredCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new ConversationManagementException(409, WhatsAppWindowExpiredMessage, WhatsAppWindowExpiredCode);
            }

            var updated = await _conversationRepository.ReabrirConversaAsync(requestedConversationId, idEstabelecimento);
            if (!updated)
            {
                throw new ConversationManagementException(404, "Conversa nao encontrada.");
            }

            await RegistrarEventoAsync(
                controleAtual.ConversationId,
                idEstabelecimento,
                "reopened",
                actorName,
                controleAtual.Status,
                "com_bot",
                request?.Motivo,
                null,
                string.IsNullOrWhiteSpace(request?.Origem) ? "api" : request!.Origem,
                actorUserId,
                null);

            return await BuildResponseAsync(requestedConversationId, idEstabelecimento);
        }

        public async Task<ConversationActionResponseDto> MarkAsReadAsync(Guid requestedConversationId, Guid idEstabelecimento)
        {
            var updated = await _conversationRepository.MarcarComoLidaAsync(requestedConversationId, idEstabelecimento);
            if (!updated)
            {
                throw new ConversationManagementException(404, "Conversa nao encontrada.");
            }

            return await BuildResponseAsync(requestedConversationId, idEstabelecimento);
        }

        public async Task<ConversationActionResponseDto> SendMessageAsync(
            Guid requestedConversationId,
            Guid idEstabelecimento,
            int? actorUserId,
            string? actorName,
            SendConversationMessageRequest? request)
        {
            var texto = request?.Mensagem?.Trim();
            if (string.IsNullOrWhiteSpace(texto))
            {
                throw new ConversationManagementException(422, "Mensagem e obrigatoria.");
            }

            var controleAtual = await EnsureControlAsync(requestedConversationId, idEstabelecimento);
            EnsureNotClosed(controleAtual);

            if (!StatusHumanos.Contains(controleAtual.Status))
            {
                throw new ConversationManagementException(409, "Envio manual so e permitido em atendimento humano aberto.");
            }

            if (!controleAtual.CanManualReply)
            {
                if (string.Equals(controleAtual.SendBlockReasonCode, WhatsAppWindowExpiredCode, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ConversationManagementException(409, WhatsAppWindowExpiredMessage, WhatsAppWindowExpiredCode);
                }

                throw new ConversationManagementException(409, "Envio manual indisponivel para esta conversa no momento.");
            }

            var conversaOperacional = await _conversationRepository.ObterPorIdAsync(controleAtual.ConversationId);
            if (conversaOperacional == null)
            {
                throw new ConversationManagementException(404, "Conversa operacional nao encontrada.");
            }

            var numeroDestino = await _clienteRepository.ObterTelefoneClienteAsync(
                conversaOperacional.IdCliente,
                conversaOperacional.IdEstabelecimento);

            if (string.IsNullOrWhiteSpace(numeroDestino))
            {
                throw new ConversationManagementException(422, "Telefone do cliente nao encontrado para envio.");
            }

            var displayPhone = await _wabaPhoneRepository.ObterDisplayPhonePorEstabelecimentoAsync(conversaOperacional.IdEstabelecimento);
            var phoneNumberId = await _wabaPhoneRepository.ObterPhoneNumberIdPorEstabelecimentoAsync(conversaOperacional.IdEstabelecimento);

            // Heranca de configuracao WABA para roteamento central (Samurai)
            if ((string.IsNullOrWhiteSpace(displayPhone) || string.IsNullOrWhiteSpace(phoneNumberId))
                && conversaOperacional.IdConversaGrupo != Guid.Empty
                && conversaOperacional.IdConversaGrupo != conversaOperacional.IdConversa)
            {
                var conversaRaiz = await _conversationRepository.ObterPorIdAsync(conversaOperacional.IdConversaGrupo);
                if (conversaRaiz != null && conversaRaiz.IdEstabelecimento != Guid.Empty)
                {
                    if (string.IsNullOrWhiteSpace(displayPhone))
                    {
                        displayPhone = await _wabaPhoneRepository.ObterDisplayPhonePorEstabelecimentoAsync(conversaRaiz.IdEstabelecimento);
                    }

                    if (string.IsNullOrWhiteSpace(phoneNumberId))
                    {
                        phoneNumberId = await _wabaPhoneRepository.ObterPhoneNumberIdPorEstabelecimentoAsync(conversaRaiz.IdEstabelecimento);
                    }
                }
            }

            phoneNumberId ??= _configuration["Automation:Meta:PhoneNumberId"];

            var criadoPor = !string.IsNullOrWhiteSpace(actorName)
                ? actorName!.Trim()
                : controleAtual.AssignedAgentName ?? "atendente";

            var agora = DateTime.UtcNow;
            var mensagem = new Message
            {
                Id = Guid.NewGuid(),
                IdConversa = controleAtual.ConversationId,
                IdMensagemWa = $"manual-{Guid.NewGuid():N}",
                Direcao = DirecaoMensagem.Saida,
                Conteudo = texto,
                DataHora = agora,
                DataCriacao = agora,
                DataEnvio = agora,
                CriadaPor = criadoPor,
                TipoOriginal = "text",
                Tipo = MessageTypeMapper.MapType("text", DirecaoMensagem.Saida, criadoPor),
                Status = "fila"
            };

            var persisted = await _messageService.AdicionarMensagemAsync(mensagem, displayPhone, numeroDestino);
            if (persisted == null)
            {
                throw new ConversationManagementException(409, "Nao foi possivel persistir a mensagem.");
            }

            await _queueBus.PublicarSaidaAsync(mensagem);

            if (string.IsNullOrWhiteSpace(phoneNumberId))
            {
                await _messageService.AtualizarStatusAsync(mensagem.Id, "falhou", "waba_not_configured", "PhoneNumberId nao configurado para o estabelecimento.");
                throw new ConversationManagementException(422, "Configuracao do WhatsApp Business ausente para este estabelecimento.");
            }

            try
            {
                await _whatsAppSender.SendTextAsync(mensagem.IdConversa, phoneNumberId, numeroDestino, texto, displayPhone);
                await _messageService.AtualizarStatusAsync(mensagem.Id, MessageStatusMapper.Enviada);
                mensagem.Status = MessageStatusMapper.Enviada;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao enviar mensagem manual da conversa {Conversa}", mensagem.IdConversa);
                await _messageService.AtualizarStatusAsync(mensagem.Id, "falhou", "send_failed", ex.Message);
                mensagem.Status = "falhou";
                throw new ConversationManagementException(422, "Falha ao enviar mensagem pelo WhatsApp.");
            }

            return await BuildResponseAsync(
                requestedConversationId,
                idEstabelecimento,
                new ConversationMessageItemDto
                {
                    Id = mensagem.Id,
                    CriadaPor = mensagem.CriadaPor ?? string.Empty,
                    Conteudo = mensagem.Conteudo,
                    Tipo = string.IsNullOrWhiteSpace(mensagem.Tipo) ? "texto" : mensagem.Tipo!,
                    Status = mensagem.Status ?? MessageStatusMapper.Enviada,
                    DataEnvio = mensagem.DataEnvio,
                    DataCriacao = mensagem.DataCriacao ?? agora
                });
        }

        private async Task<ConversationControlDto> EnsureControlAsync(Guid requestedConversationId, Guid idEstabelecimento)
        {
            var controle = await _conversationRepository.ObterControleConversaAsync(requestedConversationId, idEstabelecimento);
            if (controle == null)
            {
                throw new ConversationManagementException(404, "Conversa nao encontrada.");
            }

            return controle;
        }

        private static void EnsureNotClosed(ConversationControlDto controle)
        {
            if (StatusAbertos.Contains(controle.Status))
            {
                return;
            }

            if (string.Equals(controle.SendBlockReasonCode, WhatsAppWindowExpiredCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new ConversationManagementException(409, WhatsAppWindowExpiredMessage, WhatsAppWindowExpiredCode);
            }

            throw new ConversationManagementException(409, "Conversa encerrada. Reabra antes de continuar.");
        }

        private async Task<HandoverAgentDto?> ResolveAgentAsync(int? idAgente, int? actorUserId, string? fallbackName)
        {
            HandoverAgentDto? agente = null;

            if (idAgente.HasValue && idAgente.Value > 0)
            {
                agente = await _agenteService.ObterAgentePorIdAsync(idAgente.Value);
            }

            if (agente == null && actorUserId.HasValue && actorUserId.Value > 0)
            {
                agente = await _agenteService.ObterAgentePorUsuarioIdAsync(actorUserId.Value);
            }

            if (agente != null && string.IsNullOrWhiteSpace(agente.Nome) && !string.IsNullOrWhiteSpace(fallbackName))
            {
                agente.Nome = fallbackName;
            }

            return agente;
        }

        private async Task RegistrarEventoAsync(
            Guid conversationId,
            Guid idEstabelecimento,
            string tipo,
            string? actorName,
            string? fromStatus,
            string? toStatus,
            string? reason,
            string? closeType,
            string? source,
            int? actorUserId,
            int? actorAgentId,
            Dictionary<string, object?>? data = null)
        {
            var evento = new ConversationEventDto
            {
                Id = Guid.NewGuid(),
                Type = tipo,
                At = DateTime.UtcNow,
                ActorName = string.IsNullOrWhiteSpace(actorName) ? null : actorName.Trim(),
                FromStatus = fromStatus,
                ToStatus = toStatus,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                CloseType = string.IsNullOrWhiteSpace(closeType) ? null : closeType.Trim(),
                Source = string.IsNullOrWhiteSpace(source) ? "api" : source.Trim(),
                ActorUserId = actorUserId,
                ActorAgentId = actorAgentId,
                Data = data
            };

            await _conversationRepository.RegistrarEventoAsync(conversationId, evento, idEstabelecimento);
        }

        private async Task<ConversationActionResponseDto> BuildResponseAsync(
            Guid requestedConversationId,
            Guid idEstabelecimento,
            ConversationMessageItemDto? mensagem = null)
        {
            var detalhes = await _conversationRepository.ObterDetalhesConversaAsync(requestedConversationId, idEstabelecimento);
            var controle = await _conversationRepository.ObterControleConversaAsync(requestedConversationId, idEstabelecimento);
            var eventos = await _conversationRepository.ListarEventosConversaAsync(requestedConversationId, idEstabelecimento);

            return new ConversationActionResponseDto
            {
                Conversa = detalhes,
                Controle = controle,
                Eventos = eventos,
                Mensagem = mensagem
            };
        }

        private static string NormalizeStatus(string? status)
            => (status ?? string.Empty).Trim().ToLowerInvariant();

        private static string NormalizeCloseType(string? tipo)
            => string.IsNullOrWhiteSpace(tipo) ? "manual" : tipo.Trim().ToLowerInvariant();

        private async Task SyncServicoAtendimentoStatusAsync(Guid conversationId, string conversationStatus, string? closeType = null)
        {
            var atendimento = await _servicoAtendimentoRepository.ObterPorConversaAsync(conversationId);
            if (atendimento == null)
            {
                return;
            }

            string? novoStatus = null;
            if (!string.IsNullOrWhiteSpace(closeType))
            {
                novoStatus = string.Equals(closeType, "inatividade", StringComparison.OrdinalIgnoreCase)
                    ? "cancelado"
                    : "concluido";
            }
            else
            {
                novoStatus = NormalizeStatus(conversationStatus) switch
                {
                    "em_andamento" => "em_andamento",
                    "aguardando_interno" => "aguardando_interno",
                    "aguardando_cliente" => "aguardando_cliente",
                    "com_bot" => "aguardando_cliente",
                    _ => null
                };
            }

            if (string.IsNullOrWhiteSpace(novoStatus) ||
                string.Equals(atendimento.Status, novoStatus, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await _servicoAtendimentoRepository.AtualizarStatusAsync(atendimento.Id, novoStatus);
        }
    }
}
