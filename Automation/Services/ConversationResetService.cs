using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class ConversationResetService
    {
        private readonly IConversationRepository _conversationRepository;
        private readonly IClienteRepository _clienteRepository;
        private readonly IEstabelecimentoRepository _estabelecimentoRepository;
        private readonly IGaragemLeadRepository _garagemLeadRepository;
        private readonly INauticaLeadRepository _nauticaLeadRepository;
        private readonly CentralRoutingService _centralRouting;
        private readonly GarageFlowService _garageFlow;
        private readonly NauticaFlowService _nauticaFlow;
        private readonly ILogger<ConversationResetService> _logger;

        public ConversationResetService(
            IConversationRepository conversationRepository,
            IClienteRepository clienteRepository,
            IEstabelecimentoRepository estabelecimentoRepository,
            IGaragemLeadRepository garagemLeadRepository,
            INauticaLeadRepository nauticaLeadRepository,
            CentralRoutingService centralRouting,
            GarageFlowService garageFlow,
            NauticaFlowService nauticaFlow,
            ILogger<ConversationResetService> logger)
        {
            _conversationRepository = conversationRepository;
            _clienteRepository = clienteRepository;
            _estabelecimentoRepository = estabelecimentoRepository;
            _garagemLeadRepository = garagemLeadRepository;
            _nauticaLeadRepository = nauticaLeadRepository;
            _centralRouting = centralRouting;
            _garageFlow = garageFlow;
            _nauticaFlow = nauticaFlow;
            _logger = logger;
        }

        public bool IsResetCommand(string? mensagemTexto)
            => _centralRouting.IsResetCommand(mensagemTexto);

        public async Task<AssistantDecision> ResetAndBuildReplyAsync(Guid idConversa, string? phoneNumberDisplay)
        {
            await GarantirBotAtivoAsync(idConversa);
            var contexto = await ResetarEstadoInternoAsync(idConversa);

            if (_centralRouting.IsCentralDisplayPhone(phoneNumberDisplay))
            {
                return await CriarMenuCentralAsync(contexto.RootConversationId);
            }

            var (garageIntercepted, garageDecision) = await _garageFlow.TryHandleAsync(
                contexto.CurrentConversationId,
                string.Empty,
                phoneNumberDisplay);
            if (garageIntercepted && garageDecision != null)
            {
                return garageDecision;
            }

            var (nauticaIntercepted, nauticaDecision) = await _nauticaFlow.TryHandleAsync(
                contexto.CurrentConversationId,
                string.Empty,
                phoneNumberDisplay);
            if (nauticaIntercepted && nauticaDecision != null)
            {
                return nauticaDecision;
            }

            return CriarMensagemGenericaReset();
        }

        public async Task ResetAfterManualCloseAsync(Guid idConversa)
        {
            await ResetarEstadoInternoAsync(idConversa);
        }

        private async Task GarantirBotAtivoAsync(Guid idConversa)
        {
            var controle = await _conversationRepository.ObterControleConversaAsync(idConversa);
            if (controle == null || controle.CanBotReply)
            {
                return;
            }

            await _conversationRepository.VoltarParaBotAsync(idConversa);
        }

        private async Task<ResetContext> ResetarEstadoInternoAsync(Guid idConversa)
        {
            var atual = await _conversationRepository.ObterPorIdAsync(idConversa);
            if (atual == null)
            {
                return new ResetContext(idConversa, idConversa);
            }

            var rootConversationId = ObterRootConversationId(atual);
            var idsParaLimpar = new HashSet<Guid> { atual.IdConversa, rootConversationId };

            var contextoAtual = await _conversationRepository.ObterContextoAsync(atual.IdConversa);
            var contextoRoot = atual.IdConversa == rootConversationId
                ? contextoAtual
                : await _conversationRepository.ObterContextoAsync(rootConversationId);

            var snapshotAtual = CentralRoutingService.BuildSnapshot(contextoAtual);
            var snapshotRoot = CentralRoutingService.BuildSnapshot(contextoRoot);
            var segmentoAtivoId = snapshotAtual.ConversaSegmentoAtivaId ?? snapshotRoot.ConversaSegmentoAtivaId;
            if (segmentoAtivoId.HasValue && segmentoAtivoId.Value != Guid.Empty)
            {
                idsParaLimpar.Add(segmentoAtivoId.Value);
            }

            var conversaOperacional = await ResolverConversaOperacionalAsync(atual, segmentoAtivoId);
            var telefoneCliente = await ResolverTelefoneClienteAsync(conversaOperacional, atual);
            if (!string.IsNullOrWhiteSpace(telefoneCliente))
            {
                await CancelarLeadAbertoAsync(conversaOperacional.IdEstabelecimento, telefoneCliente!);
            }

            foreach (var conversationId in idsParaLimpar)
            {
                await _conversationRepository.LimparContextoCompletoAsync(conversationId);
            }

            _logger.LogInformation(
                "[Conversa={Conversa}] Reset completo aplicado. Root={Root} Operacional={Operacional}",
                idConversa,
                rootConversationId,
                conversaOperacional.IdConversa);

            return new ResetContext(conversaOperacional.IdConversa, rootConversationId);
        }

        private async Task<Conversation> ResolverConversaOperacionalAsync(Conversation atual, Guid? segmentoAtivoId)
        {
            if (segmentoAtivoId.HasValue && segmentoAtivoId.Value != Guid.Empty)
            {
                var segmento = await _conversationRepository.ObterPorIdAsync(segmentoAtivoId.Value);
                if (segmento != null)
                {
                    return segmento;
                }
            }

            return atual;
        }

        private async Task<string?> ResolverTelefoneClienteAsync(Conversation principal, Conversation fallback)
        {
            if (!string.IsNullOrWhiteSpace(principal.TelefoneCliente))
            {
                return principal.TelefoneCliente;
            }

            if (principal.IdCliente != Guid.Empty && principal.IdEstabelecimento != Guid.Empty)
            {
                var telefone = await _clienteRepository.ObterTelefoneClienteAsync(principal.IdCliente, principal.IdEstabelecimento);
                if (!string.IsNullOrWhiteSpace(telefone))
                {
                    return telefone;
                }
            }

            if (!string.IsNullOrWhiteSpace(fallback.TelefoneCliente))
            {
                return fallback.TelefoneCliente;
            }

            if (fallback.IdCliente != Guid.Empty && fallback.IdEstabelecimento != Guid.Empty)
            {
                return await _clienteRepository.ObterTelefoneClienteAsync(fallback.IdCliente, fallback.IdEstabelecimento);
            }

            return null;
        }

        private async Task CancelarLeadAbertoAsync(Guid idEstabelecimento, string telefoneE164)
        {
            var tipo = await _estabelecimentoRepository.ObterTipoEstabelecimentoAsync(idEstabelecimento);
            if (string.Equals(tipo, "garagem", StringComparison.OrdinalIgnoreCase))
            {
                await _garagemLeadRepository.CancelarLeadAbertoAsync(idEstabelecimento, telefoneE164);
                return;
            }

            if (string.Equals(tipo, "nautica", StringComparison.OrdinalIgnoreCase))
            {
                await _nauticaLeadRepository.CancelarLeadAbertoAsync(idEstabelecimento, telefoneE164);
            }
        }

        private async Task<AssistantDecision> CriarMenuCentralAsync(Guid rootConversationId)
        {
            var estabelecimentos = await _centralRouting.ListarEstabelecimentosElegiveisAsync();
            var mensagem = _centralRouting.BuildSelectionMenuMessage(estabelecimentos, reiniciado: true);
            await _centralRouting.SalvarMenuEscolhaAsync(rootConversationId, estabelecimentos);
            return new AssistantDecision(mensagem, "none", null, false, null, null);
        }

        private static AssistantDecision CriarMensagemGenericaReset()
        {
            return new AssistantDecision(
                "Atendimento reiniciado.\n\nComo posso te ajudar agora?",
                "none",
                null,
                false,
                null);
        }

        private static Guid ObterRootConversationId(Conversation conversa)
        {
            return conversa.IdConversaGrupo == Guid.Empty
                ? conversa.IdConversa
                : conversa.IdConversaGrupo;
        }

        private sealed record ResetContext(Guid CurrentConversationId, Guid RootConversationId);
    }
}
