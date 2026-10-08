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
    public class ConversationManagementService : IConversationCloser
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
        private readonly APIBack.Atendimento.ICanalRepository _canais;
        private readonly APIBack.Atendimento.IChatRealtimePublisher _tempoReal;
        private readonly WhatsAppSender _whatsAppSender;
        private readonly AgenteService _agenteService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ConversationManagementService> _logger;

        public ConversationManagementService(
            IConversationRepository conversationRepository,
            IMessageService messageService,
            IClienteRepository clienteRepository,
            IWabaPhoneRepository wabaPhoneRepository,
            APIBack.Atendimento.ICanalRepository canais,
            APIBack.Atendimento.IChatRealtimePublisher tempoReal,
            WhatsAppSender whatsAppSender,
            AgenteService agenteService,
            IConfiguration configuration,
            ILogger<ConversationManagementService> logger)
        {
            _conversationRepository = conversationRepository;
            _messageService = messageService;
            _clienteRepository = clienteRepository;
            _wabaPhoneRepository = wabaPhoneRepository;
            _canais = canais;
            _tempoReal = tempoReal;
            _whatsAppSender = whatsAppSender;
            _agenteService = agenteService;
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
                var requerAgenteAtribuido = !string.Equals(status, "aguardando_interno", StringComparison.OrdinalIgnoreCase);

                if (!agenteId.HasValue && requerAgenteAtribuido)
                {
                    var agente = await ResolveAgentAsync(null, actorUserId, null);
                    agenteId = agente?.Id;
                }

                if (!agenteId.HasValue && requerAgenteAtribuido)
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

        public async Task<ConversationActionResponseDto> SendMediaAsync(
            Guid requestedConversationId,
            Guid idEstabelecimento,
            int? actorUserId,
            string? actorName,
            SendConversationMediaRequest? request)
        {
            var url = request?.Url?.Trim();
            if (string.IsNullOrWhiteSpace(url))
                throw new ConversationManagementException(422, "URL do arquivo e obrigatoria.");

            var controleAtual = await EnsureControlAsync(requestedConversationId, idEstabelecimento);
            EnsureNotClosed(controleAtual);

            if (!StatusHumanos.Contains(controleAtual.Status))
                throw new ConversationManagementException(409, "Envio manual so e permitido em atendimento humano aberto.");

            if (!controleAtual.CanManualReply)
            {
                if (string.Equals(controleAtual.SendBlockReasonCode, WhatsAppWindowExpiredCode, StringComparison.OrdinalIgnoreCase))
                    throw new ConversationManagementException(409, WhatsAppWindowExpiredMessage, WhatsAppWindowExpiredCode);

                throw new ConversationManagementException(409, "Envio manual indisponivel para esta conversa no momento.");
            }

            var conversaOperacional = await _conversationRepository.ObterPorIdAsync(controleAtual.ConversationId);
            if (conversaOperacional == null)
                throw new ConversationManagementException(404, "Conversa operacional nao encontrada.");

            var numeroDestino = await _clienteRepository.ObterTelefoneClienteAsync(
                conversaOperacional.IdCliente, conversaOperacional.IdEstabelecimento);

            if (string.IsNullOrWhiteSpace(numeroDestino))
                throw new ConversationManagementException(422, "Telefone do cliente nao encontrado para envio.");

            var (displayPhone, phoneNumberId) = await ResolverNumeroDeEnvioAsync(conversaOperacional);


            var criadoPor = !string.IsNullOrWhiteSpace(actorName)
                ? actorName!.Trim()
                : controleAtual.AssignedAgentName ?? "atendente";

            var contentType = request?.ContentType;
            var nome = request?.Nome?.Trim();
            var isImage = !string.IsNullOrWhiteSpace(contentType)
                ? contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                : Path.GetExtension(url).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif";

            var tipoOriginal = isImage ? "image" : "document";
            var agora = DateTime.UtcNow;
            var conteudo = !string.IsNullOrWhiteSpace(request?.Legenda) ? request.Legenda.Trim() : url;

            var mensagem = new Message
            {
                Id = Guid.NewGuid(),
                IdConversa = controleAtual.ConversationId,
                IdMensagemWa = $"manual-{Guid.NewGuid():N}",
                Direcao = DirecaoMensagem.Saida,
                Conteudo = conteudo,
                DataHora = agora,
                DataCriacao = agora,
                DataEnvio = agora,
                CriadaPor = criadoPor,
                TipoOriginal = tipoOriginal,
                Tipo = MessageTypeMapper.MapType(tipoOriginal, DirecaoMensagem.Saida, criadoPor),
                Status = "fila"
            };

            var persisted = await _messageService.AdicionarMensagemAsync(mensagem, displayPhone, numeroDestino);
            if (persisted == null)
                throw new ConversationManagementException(409, "Nao foi possivel persistir a mensagem.");

            if (string.IsNullOrWhiteSpace(phoneNumberId))
            {
                await _messageService.AtualizarStatusAsync(mensagem.Id, "falhou", "waba_not_configured", "PhoneNumberId nao configurado.");
                throw new ConversationManagementException(422, "Configuracao do WhatsApp Business ausente para este estabelecimento.");
            }

            try
            {
                if (isImage)
                    await _whatsAppSender.SendImageAsync(mensagem.IdConversa, phoneNumberId, numeroDestino, url, displayPhone, mensagem.Id);
                else
                    await _whatsAppSender.SendDocumentAsync(mensagem.IdConversa, phoneNumberId, numeroDestino, url, nome, displayPhone, mensagem.Id);

                await _messageService.AtualizarStatusAsync(mensagem.Id, MessageStatusMapper.Enviada);
                mensagem.Status = MessageStatusMapper.Enviada;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao enviar midia manual da conversa {Conversa}", mensagem.IdConversa);
                await _messageService.AtualizarStatusAsync(mensagem.Id, "falhou", "send_failed", ex.Message);
                mensagem.Status = "falhou";
                throw new ConversationManagementException(422, $"Falha ao enviar arquivo pelo WhatsApp: {ex.Message}");
            }

            return await BuildResponseAsync(
                requestedConversationId,
                idEstabelecimento,
                new ConversationMessageItemDto
                {
                    Id = mensagem.Id,
                    CriadaPor = mensagem.CriadaPor ?? string.Empty,
                    Conteudo = mensagem.Conteudo,
                    Tipo = string.IsNullOrWhiteSpace(mensagem.Tipo) ? tipoOriginal : mensagem.Tipo!,
                    Status = mensagem.Status ?? MessageStatusMapper.Enviada,
                    DataEnvio = mensagem.DataEnvio,
                    DataCriacao = mensagem.DataCriacao ?? agora
                });
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

            var (displayPhone, phoneNumberId) = await ResolverNumeroDeEnvioAsync(conversaOperacional);


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

            if (string.IsNullOrWhiteSpace(phoneNumberId))
            {
                await _messageService.AtualizarStatusAsync(mensagem.Id, "falhou", "waba_not_configured", "PhoneNumberId nao configurado para o estabelecimento.");
                throw new ConversationManagementException(422, "Configuracao do WhatsApp Business ausente para este estabelecimento.");
            }

            try
            {
                await _whatsAppSender.SendTextAsync(mensagem.IdConversa, phoneNumberId, numeroDestino, texto, displayPhone, mensagem.Id);
                await _messageService.AtualizarStatusAsync(mensagem.Id, MessageStatusMapper.Enviada);
                mensagem.Status = MessageStatusMapper.Enviada;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao enviar mensagem manual da conversa {Conversa}", mensagem.IdConversa);
                await _messageService.AtualizarStatusAsync(mensagem.Id, "falhou", "send_failed", ex.Message);
                mensagem.Status = "falhou";
                throw new ConversationManagementException(422, $"Falha ao enviar mensagem pelo WhatsApp: {ex.Message}");
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

        /// <summary>
        /// Aviso automatico ao cliente (Fase 6) ou mensagem do motoboy ao cliente (Fase D do fluxo de motoboy):
        /// grava a mensagem na conversa, marcada por <c>criadaPor</c> ("sistema" por padrao, ou o nome do
        /// motoboy), e envia pelo WhatsApp SEM exigir que a conversa esteja assumida por um atendente. A janela
        /// de 24h continua valendo: fora dela a conversa nao aceita texto livre e o envio e recusado (409).
        /// </summary>
        /// <summary>
        /// Numero pelo qual a resposta sai: o numero (canal) em que a conversa nasceu, para o cliente receber a resposta
        /// no mesmo WhatsApp que ele procurou. Conversas antigas, sem canal, usam o primeiro numero em uso da loja.
        /// </summary>
        private async Task<(string? DisplayPhone, string? PhoneNumberId)> ResolverNumeroDeEnvioAsync(Conversation conversa)
        {
            if (conversa.IdCanal.HasValue)
            {
                var canal = await _canais.ObterAsync(conversa.IdCanal.Value);
                if (canal != null && canal.Status != APIBack.Atendimento.StatusCanal.Inativo)
                {
                    return (canal.NumeroE164, canal.PhoneNumberId);
                }
            }

            var display = await _wabaPhoneRepository.ObterDisplayPhonePorEstabelecimentoAsync(conversa.IdEstabelecimento);
            var phoneNumberId = await _wabaPhoneRepository.ObterPhoneNumberIdPorEstabelecimentoAsync(conversa.IdEstabelecimento);
            return (display, phoneNumberId);
        }

        public async Task<Guid> SendSystemNoticeAsync(Guid requestedConversationId, Guid idEstabelecimento, string texto, string criadaPor = "sistema")
        {
            if (string.IsNullOrWhiteSpace(texto))
            {
                throw new ConversationManagementException(422, "Mensagem e obrigatoria.");
            }

            var controle = await EnsureControlAsync(requestedConversationId, idEstabelecimento);
            if (string.Equals(controle.SendBlockReasonCode, WhatsAppWindowExpiredCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new ConversationManagementException(409, WhatsAppWindowExpiredMessage, WhatsAppWindowExpiredCode);
            }

            var conversa = await _conversationRepository.ObterPorIdAsync(controle.ConversationId)
                ?? throw new ConversationManagementException(404, "Conversa operacional nao encontrada.");
            var numeroDestino = await _clienteRepository.ObterTelefoneClienteAsync(conversa.IdCliente, conversa.IdEstabelecimento);
            if (string.IsNullOrWhiteSpace(numeroDestino))
            {
                throw new ConversationManagementException(422, "Telefone do cliente nao encontrado para envio.");
            }

            var (displayPhone, phoneNumberId) = await ResolverNumeroDeEnvioAsync(conversa);

            var agora = DateTime.UtcNow;
            var mensagem = new Message
            {
                Id = Guid.NewGuid(),
                IdConversa = controle.ConversationId,
                IdMensagemWa = $"notice-{Guid.NewGuid():N}",
                Direcao = DirecaoMensagem.Saida,
                Conteudo = texto.Trim(),
                DataHora = agora,
                DataCriacao = agora,
                DataEnvio = agora,
                CriadaPor = criadaPor,
                TipoOriginal = "text",
                Tipo = MessageTypeMapper.MapType("text", DirecaoMensagem.Saida, criadaPor),
                Status = "fila"
            };

            var persisted = await _messageService.AdicionarMensagemAsync(mensagem, displayPhone, numeroDestino);
            if (persisted == null)
            {
                throw new ConversationManagementException(409, "Nao foi possivel persistir a mensagem.");
            }

            if (string.IsNullOrWhiteSpace(phoneNumberId))
            {
                await _messageService.AtualizarStatusAsync(mensagem.Id, "falhou", "waba_not_configured", "PhoneNumberId nao configurado para o estabelecimento.");
                throw new ConversationManagementException(422, "Configuracao do WhatsApp Business ausente para este estabelecimento.");
            }

            try
            {
                await _whatsAppSender.SendTextAsync(mensagem.IdConversa, phoneNumberId, numeroDestino, mensagem.Conteudo, displayPhone, mensagem.Id);
                await _messageService.AtualizarStatusAsync(mensagem.Id, MessageStatusMapper.Enviada);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao enviar aviso automatico da conversa {Conversa}", mensagem.IdConversa);
                await _messageService.AtualizarStatusAsync(mensagem.Id, "falhou", "send_failed", ex.Message);
                throw new ConversationManagementException(422, $"Falha ao enviar mensagem pelo WhatsApp: {ex.Message}");
            }

            return mensagem.Id;
        }

        public async Task<Guid> SendOperationalMessageAsync(Guid requestedConversationId,Guid establishmentId,string body,string sender,byte[]? media,string? contentType,string? replyProviderId)
        {
            var control=await EnsureControlAsync(requestedConversationId,establishmentId);
            EnsureNotClosed(control);
            if(control.SendBlockReasonCode==WhatsAppWindowExpiredCode) throw new ConversationManagementException(409,WhatsAppWindowExpiredMessage,WhatsAppWindowExpiredCode);
            var conversation=await _conversationRepository.ObterPorIdAsync(control.ConversationId) ?? throw new ConversationManagementException(404,"Conversa nao encontrada.");
            var destination=await _clienteRepository.ObterTelefoneClienteAsync(conversation.IdCliente,conversation.IdEstabelecimento);
            if(string.IsNullOrWhiteSpace(destination))throw new ConversationManagementException(422,"Telefone do cliente nao encontrado.");
            var (display,phoneId)=await ResolverNumeroDeEnvioAsync(conversation);
            if(string.IsNullOrWhiteSpace(phoneId))throw new ConversationManagementException(422,"WhatsApp da loja nao configurado.");
            var type=media==null?"text":contentType!.StartsWith("image/")?"image":"audio";
            var now=DateTime.UtcNow;
            var message=new Message{Id=Guid.NewGuid(),IdConversa=control.ConversationId,IdMensagemWa=$"courier-{Guid.NewGuid():N}",Direcao=DirecaoMensagem.Saida,Conteudo=string.IsNullOrEmpty(body)?type=="audio"?"[Audio]":"[Foto]":body,
                DataHora=now,DataCriacao=now,DataEnvio=now,CriadaPor=sender,TipoOriginal=type,Tipo=MessageTypeMapper.MapType(type,DirecaoMensagem.Saida,sender),Status="fila"};
            if(await _messageService.AdicionarMensagemAsync(message,display,destination)==null)throw new ConversationManagementException(409,"Nao foi possivel persistir o envio.");
            try { await _whatsAppSender.SendOperationalAsync(message.IdConversa,phoneId,destination,body,media,contentType,message.Id,replyProviderId);await _messageService.AtualizarStatusAsync(message.Id,MessageStatusMapper.Enviada); }
            catch(Exception ex){await _messageService.AtualizarStatusAsync(message.Id,"falhou","send_uncertain","Envio precisa de conferencia.");_logger.LogWarning(ex,"Envio do motoboy nao confirmado na conversa {ConversationId}",message.IdConversa);throw new ConversationManagementException(409,"Envio nao confirmado. Consulte a loja antes de tentar novamente.","CHAT_SEND_UNCERTAIN");}
            return message.Id;
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

            // Toda acao sobre a conversa (assumir, devolver ao bot, fechar, reabrir, ler...) avisa as telas abertas.
            await _tempoReal.ConversaAtualizadaAsync(requestedConversationId, idEstabelecimento, "acao");

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
    }
}
