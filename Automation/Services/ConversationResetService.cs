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
        private const string HardResetReason = "Reset por comando do cliente (*3275)";
        private const string ManualCloseCleanupReason = "Fechamento manual da conversa";
        private const string ExpirationRestartReason = "Reinicio automatico apos expiracao da conversa";
        private readonly IConversationRepository _conversationRepository;
        private readonly IClienteRepository _clienteRepository;
        private readonly IEstabelecimentoRepository _estabelecimentoRepository;
        private readonly IOficinaAtendimentoRepository _oficinaAtendimentoRepository;
        private readonly IServicoAtendimentoRepository _servicoAtendimentoRepository;
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
            IOficinaAtendimentoRepository oficinaAtendimentoRepository,
            IServicoAtendimentoRepository servicoAtendimentoRepository,
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
            _oficinaAtendimentoRepository = oficinaAtendimentoRepository;
            _servicoAtendimentoRepository = servicoAtendimentoRepository;
            _garagemLeadRepository = garagemLeadRepository;
            _nauticaLeadRepository = nauticaLeadRepository;
            _centralRouting = centralRouting;
            _garageFlow = garageFlow;
            _nauticaFlow = nauticaFlow;
            _logger = logger;
        }

        public bool IsResetCommand(string? mensagemTexto)
            => _centralRouting.IsResetCommand(mensagemTexto);

        public sealed record ResetReply(Guid TargetConversationId, AssistantDecision Decision);

        public async Task<ResetReply> ResetAndBuildReplyAsync(Guid idConversa, string? phoneNumberDisplay)
        {
            await GarantirBotAtivoAsync(idConversa);
            var ehNumeroCentral = _centralRouting.IsCentralDisplayPhone(phoneNumberDisplay);
            var contexto = await ResetarEstadoInternoAsync(idConversa, ehNumeroCentral);

            if (ehNumeroCentral)
            {
                var decisionCentral = await CriarMenuCentralAsync(contexto.NewConversationId);
                return new ResetReply(contexto.NewConversationId, decisionCentral);
            }

            var (garageIntercepted, garageDecision) = await _garageFlow.TryHandleAsync(
                contexto.NewConversationId,
                string.Empty,
                phoneNumberDisplay);
            if (garageIntercepted && garageDecision != null)
            {
                return new ResetReply(contexto.NewConversationId, garageDecision);
            }

            var (nauticaIntercepted, nauticaDecision) = await _nauticaFlow.TryHandleAsync(
                contexto.NewConversationId,
                string.Empty,
                phoneNumberDisplay);
            if (nauticaIntercepted && nauticaDecision != null)
            {
                return new ResetReply(contexto.NewConversationId, nauticaDecision);
            }

            return new ResetReply(contexto.NewConversationId, CriarMensagemGenericaReset());
        }

        public async Task ResetAfterManualCloseAsync(Guid idConversa)
        {
            await ResetarEstadoInternoAsync(idConversa, ehNumeroCentral: false, criarNovaConversa: false);
        }

        public async Task<Guid> RestartExpiredConversationAsync(Guid idConversa, bool ehNumeroCentral)
        {
            var contexto = await ResetarEstadoInternoAsync(
                idConversa,
                ehNumeroCentral,
                criarNovaConversa: true,
                motivoFechamento: ExpirationRestartReason);
            return contexto.NewConversationId;
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

        private async Task<ResetContext> ResetarEstadoInternoAsync(
            Guid idConversa,
            bool ehNumeroCentral,
            bool criarNovaConversa = true,
            string? motivoFechamento = null)
        {
            var atual = await _conversationRepository.ObterPorIdAsync(idConversa);
            if (atual == null)
            {
                return new ResetContext(idConversa, idConversa, idConversa);
            }

            var rootConversationId = ObterRootConversationId(atual);
            var conversaRaiz = atual.IdConversa == rootConversationId
                ? atual
                : await _conversationRepository.ObterPorIdAsync(rootConversationId) ?? atual;
            var contextoAtual = await _conversationRepository.ObterContextoAsync(atual.IdConversa);
            var contextoRoot = atual.IdConversa == rootConversationId
                ? contextoAtual
                : await _conversationRepository.ObterContextoAsync(rootConversationId);

            var snapshotAtual = CentralRoutingService.BuildSnapshot(contextoAtual);
            var snapshotRoot = CentralRoutingService.BuildSnapshot(contextoRoot);
            var segmentoAtivoId = snapshotAtual.ConversaSegmentoAtivaId ?? snapshotRoot.ConversaSegmentoAtivaId;

            var conversaOperacional = await ResolverConversaOperacionalAsync(atual, segmentoAtivoId);
            var telefoneCliente = await ResolverTelefoneClienteAsync(conversaOperacional, atual);
            var conversasAbertasMesmoTelefone = !string.IsNullOrWhiteSpace(telefoneCliente)
                ? await _conversationRepository.ListarConversasAbertasPorTelefoneAsync(telefoneCliente!)
                : Array.Empty<Conversation>();
            var gruposParaLimpar = conversasAbertasMesmoTelefone
                .Select(ObterRootConversationId)
                .Append(rootConversationId)
                .Distinct()
                .ToList();
            await CancelarEstadosRecuperaveisAsync(
                conversaOperacional.IdEstabelecimento,
                conversaOperacional.IdConversa,
                telefoneCliente);

            foreach (var groupId in gruposParaLimpar)
            {
                await _conversationRepository.LimparContextoGrupoCompletoAsync(groupId);
            }

            var motivo = string.IsNullOrWhiteSpace(motivoFechamento)
                ? (criarNovaConversa ? HardResetReason : ManualCloseCleanupReason)
                : motivoFechamento!;

            if (!criarNovaConversa)
            {
                if (!string.IsNullOrWhiteSpace(telefoneCliente))
                {
                    await _conversationRepository.FecharConversasAbertasPorTelefoneAsync(
                        telefoneCliente!,
                        idAgente: null,
                        motivo: motivo,
                        tipoFechamento: "manual");
                }
                else
                {
                    await _conversationRepository.FecharGrupoConversaAsync(
                        rootConversationId,
                        idAgente: null,
                        motivo: motivo,
                        tipoFechamento: "manual");
                }

                _logger.LogInformation(
                    "[Conversa={Conversa}] Limpeza de contexto aplicada apos fechamento manual. RootAntiga={RootAntiga} OperacionalAntiga={OperacionalAntiga}",
                    idConversa,
                    rootConversationId,
                    conversaOperacional.IdConversa);

                return new ResetContext(conversaOperacional.IdConversa, rootConversationId, Guid.Empty);
            }

            var novaConversa = await CriarNovaConversaAsync(
                conversaRaiz,
                conversaOperacional,
                telefoneCliente,
                ehNumeroCentral);

            if (!string.IsNullOrWhiteSpace(telefoneCliente))
            {
                await _conversationRepository.ReiniciarConversaPorTelefoneAsync(
                    telefoneCliente!,
                    novaConversa,
                    idAgente: null,
                    motivo: motivo,
                    tipoFechamento: "manual");
            }
            else
            {
                await _conversationRepository.FecharGrupoConversaAsync(
                    rootConversationId,
                    idAgente: null,
                    motivo: motivo,
                    tipoFechamento: "manual");
                await _conversationRepository.InserirOuAtualizarAsync(novaConversa);
            }

            _logger.LogInformation(
                "[Conversa={Conversa}] Reinicio interno aplicado. Motivo={Motivo} RootAntiga={RootAntiga} OperacionalAntiga={OperacionalAntiga} NovaConversa={NovaConversa}",
                idConversa,
                motivo,
                rootConversationId,
                conversaOperacional.IdConversa,
                novaConversa.IdConversa);

            return new ResetContext(conversaOperacional.IdConversa, rootConversationId, novaConversa.IdConversa);
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

        private async Task CancelarEstadosRecuperaveisAsync(Guid idEstabelecimento, Guid idConversa, string? telefoneE164)
        {
            if (!string.IsNullOrWhiteSpace(telefoneE164))
            {
                await CancelarLeadAbertoAsync(idEstabelecimento, telefoneE164!);
            }

            await CancelarAtendimentoServicoAsync(idEstabelecimento, idConversa, telefoneE164);
            await CancelarAtendimentoOficinaAsync(idEstabelecimento, idConversa, telefoneE164);
        }

        private async Task CancelarAtendimentoServicoAsync(Guid idEstabelecimento, Guid idConversa, string? telefoneE164)
        {
            var atendimento = await _servicoAtendimentoRepository.ObterPorConversaAsync(idConversa);
            if (atendimento == null &&
                !string.IsNullOrWhiteSpace(telefoneE164) &&
                idEstabelecimento != Guid.Empty)
            {
                atendimento = await _servicoAtendimentoRepository.ObterAbertoAsync(idEstabelecimento, telefoneE164!);
            }

            if (atendimento != null && IsAtendimentoAberto(atendimento.Status))
            {
                await _servicoAtendimentoRepository.AtualizarStatusAsync(atendimento.Id, "cancelado");
            }
        }

        private async Task CancelarAtendimentoOficinaAsync(Guid idEstabelecimento, Guid idConversa, string? telefoneE164)
        {
            var atendimento = await _oficinaAtendimentoRepository.ObterPorConversaAsync(idConversa);
            if (atendimento == null &&
                !string.IsNullOrWhiteSpace(telefoneE164) &&
                idEstabelecimento != Guid.Empty)
            {
                atendimento = await _oficinaAtendimentoRepository.ObterAbertoAsync(idEstabelecimento, telefoneE164!);
            }

            if (atendimento != null && IsAtendimentoAberto(atendimento.Status))
            {
                await _oficinaAtendimentoRepository.AtualizarStatusAsync(atendimento.Id, "cancelado");
            }
        }

        private static bool IsAtendimentoAberto(string? status)
        {
            return string.Equals(status, "em_triagem", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "aguardando_cliente", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "aguardando_interno", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "em_andamento", StringComparison.OrdinalIgnoreCase);
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

        private async Task<Conversation> CriarNovaConversaAsync(
            Conversation conversaRaizAnterior,
            Conversation conversaOperacionalAnterior,
            string? telefoneCliente,
            bool ehNumeroCentral)
        {
            var baseConversa = ehNumeroCentral ? conversaRaizAnterior : conversaOperacionalAnterior;
            var idEstabelecimento = baseConversa.IdEstabelecimento;
            if (idEstabelecimento == Guid.Empty)
            {
                idEstabelecimento = conversaOperacionalAnterior.IdEstabelecimento;
            }

            var idCliente = baseConversa.IdCliente;
            if (idCliente == Guid.Empty && !string.IsNullOrWhiteSpace(telefoneCliente) && idEstabelecimento != Guid.Empty)
            {
                idCliente = await _clienteRepository.GarantirClienteAsync(telefoneCliente!, idEstabelecimento);
            }

            var agora = DateTime.UtcNow;
            var novaConversa = new Conversation
            {
                IdConversa = Guid.NewGuid(),
                IdConversaGrupo = Guid.Empty,
                IdEstabelecimento = idEstabelecimento,
                IdCliente = idCliente,
                TelefoneCliente = !string.IsNullOrWhiteSpace(telefoneCliente)
                    ? telefoneCliente
                    : baseConversa.TelefoneCliente ?? conversaOperacionalAnterior.TelefoneCliente,
                IdWa = !string.IsNullOrWhiteSpace(baseConversa.IdWa)
                    ? baseConversa.IdWa
                    : conversaOperacionalAnterior.IdWa,
                Modo = ModoConversa.Bot,
                Estado = EstadoConversa.Aberto,
                StatusAtendimento = "com_bot",
                CriadoEm = agora,
                AtualizadoEm = agora
            };

            return novaConversa;
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

        private sealed record ResetContext(Guid OldOperationalConversationId, Guid OldRootConversationId, Guid NewConversationId);
    }
}
