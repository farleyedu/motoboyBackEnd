using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;

namespace APIBack.Automation.Services
{
    public class TopicOrchestratorService
    {
        private const string EstadoAguardandoTrocaTema = "topic_aguardando_troca_assunto";
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly IConversationRepository _conversationRepository;
        private readonly IEstabelecimentoRepository _estabelecimentoRepository;
        private readonly IServicoAtendimentoRepository _servicoAtendimentoRepository;
        private readonly CentralRoutingService _centralRouting;
        private readonly ServicosFlowService _servicosFlow;
        private readonly FaqCatalogProvider _faqCatalogProvider;

        public TopicOrchestratorService(
            IConversationRepository conversationRepository,
            IEstabelecimentoRepository estabelecimentoRepository,
            IServicoAtendimentoRepository servicoAtendimentoRepository,
            CentralRoutingService centralRouting,
            ServicosFlowService servicosFlow,
            FaqCatalogProvider faqCatalogProvider)
        {
            _conversationRepository = conversationRepository;
            _estabelecimentoRepository = estabelecimentoRepository;
            _servicoAtendimentoRepository = servicoAtendimentoRepository;
            _centralRouting = centralRouting;
            _servicosFlow = servicosFlow;
            _faqCatalogProvider = faqCatalogProvider;
        }

        public async Task<(bool Intercepted, AssistantDecision? Decision)> TryHandleAsync(
            Guid idConversa,
            string mensagemTexto,
            string? phoneNumberDisplay)
        {
            var texto = mensagemTexto?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(texto))
            {
                return (false, null);
            }

            var scope = await _centralRouting.ResolveEffectiveScopeAsync(idConversa);
            if (scope == null || scope.IdEstabelecimento == Guid.Empty || scope.IdCliente == Guid.Empty)
            {
                return (false, null);
            }

            var modulosAtivos = await _estabelecimentoRepository.ObterModulosAtivosAsync(scope.IdEstabelecimento);
            var faqAtivo = modulosAtivos.Any(item => string.Equals(item, "FAQ", StringComparison.OrdinalIgnoreCase));
            var servicosAtivo = modulosAtivos.Any(item => string.Equals(item, "Servicos", StringComparison.OrdinalIgnoreCase));
            if (!faqAtivo && !servicosAtivo)
            {
                return (false, null);
            }

            var contextoAtual = await _conversationRepository.ObterContextoAsync(idConversa);
            var atendimentoAtual = servicosAtivo ? await _servicoAtendimentoRepository.ObterPorConversaAsync(idConversa) : null;
            var activeTopic = ReadTopic(contextoAtual?.DadosColetados, ConversationTopicContextKeys.Active);
            var queue = ReadTopics(contextoAtual?.DadosColetados, ConversationTopicContextKeys.Queue);
            var candidateTopic = ReadTopic(contextoAtual?.DadosColetados, ConversationTopicContextKeys.Candidate);
            var prompt = ReadSwitchPrompt(contextoAtual?.DadosColetados);

            if (activeTopic == null && servicosAtivo && _servicosFlow.HasServiceTopicState(contextoAtual, atendimentoAtual))
            {
                activeTopic = _servicosFlow.BuildActiveServiceTopic(contextoAtual, atendimentoAtual);
                if (activeTopic != null)
                {
                    await SaveContextAsync(
                        idConversa,
                        contextoAtual,
                        contextoAtual?.Estado,
                        activeTopic,
                        queue,
                        null,
                        null);
                }
            }

            if (prompt != null && candidateTopic != null)
            {
                var decision = await HandleSwitchPromptAsync(
                    idConversa,
                    contextoAtual,
                    activeTopic,
                    queue,
                    candidateTopic,
                    prompt,
                    texto,
                    phoneNumberDisplay);

                return (true, decision);
            }

            if (queue.Count > 0 && IsResumeCommand(texto) && activeTopic == null)
            {
                var decision = await ResumePausedTopicAsync(
                    idConversa,
                    contextoAtual,
                    queue);

                return (true, decision);
            }

            if (activeTopic != null &&
                string.Equals(activeTopic.Module, ConversationTopicModules.Servicos, StringComparison.OrdinalIgnoreCase) &&
                !_servicosFlow.ShouldBlockTopicSwitch(contextoAtual, atendimentoAtual, texto))
            {
                var serviceCandidate = servicosAtivo
                    ? await _servicosFlow.DetectDifferentServiceTopicAsync(scope.IdEstabelecimento, texto, activeTopic)
                    : null;
                var faqCandidate = faqAtivo
                    ? await BuildFaqCandidateAsync(scope.IdEstabelecimento, texto)
                    : null;
                var chosenCandidate = ChooseCandidate(serviceCandidate, faqCandidate);

                if (chosenCandidate != null)
                {
                    var promptDecision = await PromptTopicSwitchAsync(
                        idConversa,
                        contextoAtual,
                        activeTopic,
                        queue,
                        chosenCandidate);

                    return (true, promptDecision);
                }
            }

            if (activeTopic == null && faqAtivo)
            {
                var faqCandidate = await BuildFaqCandidateAsync(scope.IdEstabelecimento, texto);
                var serviceCandidate = servicosAtivo
                    ? await _servicosFlow.DetectDifferentServiceTopicAsync(scope.IdEstabelecimento, texto, null)
                    : null;

                if (faqCandidate != null && (serviceCandidate == null || faqCandidate.MatchScore >= serviceCandidate.MatchScore))
                {
                    var decision = await ExecuteTopicAsync(
                        idConversa,
                        contextoAtual,
                        null,
                        queue,
                        faqCandidate,
                        phoneNumberDisplay);

                    return (true, decision);
                }
            }

            return (false, null);
        }

        private async Task<AssistantDecision> HandleSwitchPromptAsync(
            Guid idConversa,
            ConversationContext? contextoAtual,
            ConversationTopicState? activeTopic,
            List<ConversationTopicState> queue,
            ConversationTopicState candidateTopic,
            ConversationTopicSwitchPrompt prompt,
            string mensagemTexto,
            string? phoneNumberDisplay)
        {
            var option = ParseSwitchOption(mensagemTexto);
            if (option == 0)
            {
                return new AssistantDecision(prompt.PromptText, "none", null, false, null);
            }

            switch (option)
            {
                case 1:
                {
                    if (activeTopic != null && string.Equals(activeTopic.Module, ConversationTopicModules.Servicos, StringComparison.OrdinalIgnoreCase))
                    {
                        var restaurado = _servicosFlow.BuildRestoredServiceContext(contextoAtual, activeTopic, queue);
                        await _conversationRepository.SalvarContextoAsync(idConversa, restaurado);
                        return new AssistantDecision(_servicosFlow.BuildServiceResumePrompt(activeTopic), "none", null, false, null);
                    }

                    await SaveContextAsync(idConversa, contextoAtual, contextoAtual?.Estado, activeTopic, queue, null, null);
                    return new AssistantDecision("Perfeito. Vamos continuar no assunto atual.", "none", null, false, null);
                }
                case 2:
                {
                    var filaAtualizada = queue;
                    if (activeTopic != null)
                    {
                        activeTopic.RuntimeStatus = ConversationTopicRuntimeStatus.Pausado;
                        activeTopic.UpdatedAt = DateTime.UtcNow;
                        filaAtualizada.Add(activeTopic);
                        filaAtualizada = filaAtualizada
                            .OrderBy(item => item.CreatedAt)
                            .TakeLast(3)
                            .ToList();
                        await CancelServiceWorkspaceIfNeededAsync(activeTopic, idConversa);
                    }

                    return await ExecuteTopicAsync(
                        idConversa,
                        contextoAtual,
                        null,
                        filaAtualizada,
                        candidateTopic,
                        phoneNumberDisplay);
                }
                case 3:
                {
                    if (activeTopic != null)
                    {
                        activeTopic.RuntimeStatus = ConversationTopicRuntimeStatus.Descartado;
                        activeTopic.UpdatedAt = DateTime.UtcNow;
                        await CancelServiceWorkspaceIfNeededAsync(activeTopic, idConversa);
                    }

                    return await ExecuteTopicAsync(
                        idConversa,
                        contextoAtual,
                        null,
                        queue,
                        candidateTopic,
                        phoneNumberDisplay);
                }
                default:
                {
                    if (activeTopic != null)
                    {
                        await CancelServiceWorkspaceIfNeededAsync(activeTopic, idConversa);
                    }

                    return await ExecuteTopicAsync(
                        idConversa,
                        contextoAtual,
                        null,
                        new List<ConversationTopicState>(),
                        candidateTopic,
                        phoneNumberDisplay);
                }
            }
        }

        private async Task<AssistantDecision> PromptTopicSwitchAsync(
            Guid idConversa,
            ConversationContext? contextoAtual,
            ConversationTopicState activeTopic,
            IReadOnlyList<ConversationTopicState> queue,
            ConversationTopicState candidateTopic)
        {
            candidateTopic.PendingMessage ??= string.Empty;
            candidateTopic.CreatedAt = DateTime.UtcNow;
            candidateTopic.UpdatedAt = candidateTopic.CreatedAt;

            var activeLabel = string.IsNullOrWhiteSpace(activeTopic.Summary) ? activeTopic.Label : activeTopic.Summary;
            var candidateLabel = string.IsNullOrWhiteSpace(candidateTopic.Summary) ? candidateTopic.Label : candidateTopic.Summary;
            var promptText =
                $"Agora estamos falando sobre {activeLabel}. Vi que voce perguntou sobre {candidateLabel}. O que voce quer fazer?\n" +
                "1. Continuar no assunto atual\n" +
                "2. Ir para o novo assunto e depois voltar\n" +
                "3. Trocar para o novo assunto e encerrar o atual\n" +
                "4. Resetar tudo e comecar do zero no novo assunto";

            var prompt = new ConversationTopicSwitchPrompt
            {
                ActiveTopicId = activeTopic.TopicId,
                CandidateTopicId = candidateTopic.TopicId,
                PromptText = promptText
            };

            await SaveContextAsync(
                idConversa,
                contextoAtual,
                EstadoAguardandoTrocaTema,
                activeTopic,
                queue,
                candidateTopic,
                prompt);

            return new AssistantDecision(promptText, "none", null, false, null);
        }

        private async Task<AssistantDecision> ExecuteTopicAsync(
            Guid idConversa,
            ConversationContext? contextoAtual,
            ConversationTopicState? activeTopic,
            IReadOnlyList<ConversationTopicState> queue,
            ConversationTopicState candidateTopic,
            string? phoneNumberDisplay)
        {
            if (string.Equals(candidateTopic.Module, ConversationTopicModules.Servicos, StringComparison.OrdinalIgnoreCase))
            {
                await SaveContextAsync(
                    idConversa,
                    contextoAtual,
                    null,
                    activeTopic,
                    queue,
                    null,
                    null,
                    limparActiveTopic: true);

                var decision = await _servicosFlow.TryHandleAsync(
                    idConversa,
                    candidateTopic.PendingMessage ?? candidateTopic.Label,
                    phoneNumberDisplay);

                if (decision.Intercepted && decision.Decision != null)
                {
                    return decision.Decision;
                }

                return new AssistantDecision("Me fala o servico que voce quer ver que eu continuo por aqui.", "none", null, false, null);
            }

            return await ExecuteFaqAsync(
                idConversa,
                contextoAtual,
                queue,
                candidateTopic);
        }

        private async Task<AssistantDecision> ExecuteFaqAsync(
            Guid idConversa,
            ConversationContext? contextoAtual,
            IReadOnlyList<ConversationTopicState> queue,
            ConversationTopicState candidateTopic)
        {
            if (!TryDeserializeFaq(candidateTopic.SnapshotJson, out var faq))
            {
                return new AssistantDecision("Nao consegui recuperar esse FAQ. Me fala de outro jeito que eu continuo por aqui.", "none", null, false, null);
            }

            var reply = faq.Resposta.Trim();
            if (queue.Count > 0)
            {
                var proximo = queue[^1];
                reply = $"{reply}\n\nSe quiser, depois eu volto para {proximo.Label}. Me responde com voltar.";
            }

            await SaveContextAsync(
                idConversa,
                contextoAtual,
                null,
                null,
                queue,
                null,
                null,
                limparActiveTopic: true);

            return new AssistantDecision(reply, "none", null, false, null);
        }

        private async Task<AssistantDecision> ResumePausedTopicAsync(
            Guid idConversa,
            ConversationContext? contextoAtual,
            List<ConversationTopicState> queue)
        {
            var topic = queue[^1];
            queue.RemoveAt(queue.Count - 1);

            if (string.Equals(topic.Module, ConversationTopicModules.Servicos, StringComparison.OrdinalIgnoreCase))
            {
                var restaurado = _servicosFlow.BuildRestoredServiceContext(contextoAtual, topic, queue);
                await _conversationRepository.SalvarContextoAsync(idConversa, restaurado);
                return new AssistantDecision(_servicosFlow.BuildServiceResumePrompt(topic), "none", null, false, null);
            }

            await SaveContextAsync(idConversa, contextoAtual, null, null, queue, null, null, limparActiveTopic: true);
            return new AssistantDecision("Voltei para o assunto pausado.", "none", null, false, null);
        }

        private async Task<ConversationTopicState?> BuildFaqCandidateAsync(Guid idEstabelecimento, string mensagemTexto)
        {
            var match = await _faqCatalogProvider.MatchStrongAsync(idEstabelecimento, mensagemTexto);
            if (match == null)
            {
                return null;
            }

            var snapshot = new FaqTopicSnapshot
            {
                FaqId = match.Item.Id,
                Pergunta = match.Item.Pergunta,
                Resposta = match.Item.Resposta,
                Categoria = match.Item.Categoria,
                MensagemOrigem = mensagemTexto.Trim()
            };

            return new ConversationTopicState
            {
                Module = ConversationTopicModules.Faq,
                Label = match.Item.Categoria,
                RuntimeStatus = ConversationTopicRuntimeStatus.Ativo,
                Summary = match.Item.Pergunta,
                SnapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions),
                PendingMessage = mensagemTexto.Trim(),
                MatchScore = match.Score,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
        }

        private async Task CancelServiceWorkspaceIfNeededAsync(ConversationTopicState? topic, Guid idConversa)
        {
            if (topic == null || !string.Equals(topic.Module, ConversationTopicModules.Servicos, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var atendimento = await _servicoAtendimentoRepository.ObterPorConversaAsync(idConversa);
            if (atendimento != null &&
                (string.Equals(atendimento.Status, "em_triagem", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(atendimento.Status, "aguardando_cliente", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(atendimento.Status, "aguardando_interno", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(atendimento.Status, "em_andamento", StringComparison.OrdinalIgnoreCase)))
            {
                await _servicoAtendimentoRepository.AtualizarStatusAsync(atendimento.Id, "cancelado");
            }
        }

        private async Task SaveContextAsync(
            Guid idConversa,
            ConversationContext? contextoAtual,
            string? estado,
            ConversationTopicState? activeTopic,
            IReadOnlyList<ConversationTopicState> queue,
            ConversationTopicState? candidate,
            ConversationTopicSwitchPrompt? switchPrompt,
            bool limparActiveTopic = false)
        {
            var dados = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (!limparActiveTopic && contextoAtual?.DadosColetados != null)
            {
                foreach (var item in contextoAtual.DadosColetados)
                {
                    if (item.Value != null &&
                        !string.Equals(item.Key, ConversationTopicContextKeys.Active, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(item.Key, ConversationTopicContextKeys.Queue, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(item.Key, ConversationTopicContextKeys.Candidate, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(item.Key, ConversationTopicContextKeys.SwitchPrompt, StringComparison.OrdinalIgnoreCase))
                    {
                        dados[item.Key] = item.Value;
                    }
                }
            }

            if (!limparActiveTopic && activeTopic != null)
            {
                activeTopic.UpdatedAt = DateTime.UtcNow;
                dados[ConversationTopicContextKeys.Active] = JsonSerializer.Serialize(activeTopic, JsonOptions);
            }

            if (queue.Count > 0)
            {
                dados[ConversationTopicContextKeys.Queue] = JsonSerializer.Serialize(queue, JsonOptions);
            }

            if (candidate != null)
            {
                candidate.UpdatedAt = DateTime.UtcNow;
                dados[ConversationTopicContextKeys.Candidate] = JsonSerializer.Serialize(candidate, JsonOptions);
            }

            if (switchPrompt != null)
            {
                dados[ConversationTopicContextKeys.SwitchPrompt] = JsonSerializer.Serialize(switchPrompt, JsonOptions);
            }

            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = estado,
                DadosColetados = dados
            });
        }

        private static ConversationTopicState? ChooseCandidate(
            ConversationTopicState? serviceCandidate,
            ConversationTopicState? faqCandidate)
        {
            if (serviceCandidate == null)
            {
                return faqCandidate;
            }

            if (faqCandidate == null)
            {
                return serviceCandidate;
            }

            return serviceCandidate.MatchScore >= faqCandidate.MatchScore ? serviceCandidate : faqCandidate;
        }

        private static int ParseSwitchOption(string mensagemTexto)
        {
            var texto = mensagemTexto.Trim().ToLowerInvariant();
            return texto switch
            {
                "1" => 1,
                "2" => 2,
                "3" => 3,
                "4" => 4,
                _ when texto == "continuar" || texto == "continuar aqui" || texto == "continuar no assunto" => 1,
                _ when texto == "depois voltar" || texto == "voltar depois" || texto == "ir e voltar" => 2,
                _ when texto == "trocar" || texto == "trocar assunto" => 3,
                _ when texto == "reset" || texto == "resetar" || texto == "comecar do zero" => 4,
                _ => 0
            };
        }

        private static bool IsResumeCommand(string texto)
        {
            var normalizado = texto.Trim().ToLowerInvariant();
            return normalizado == "voltar" ||
                   normalizado == "retomar" ||
                   normalizado.Contains("voltar para o assunto") ||
                   normalizado.Contains("continuar assunto anterior");
        }

        private static ConversationTopicState? ReadTopic(IReadOnlyDictionary<string, object>? dados, string chave)
        {
            var json = ReadString(dados, chave);
            return ServicosFlowService.TryDeserializeTopic(json, out var topic) ? topic : null;
        }

        private static List<ConversationTopicState> ReadTopics(IReadOnlyDictionary<string, object>? dados, string chave)
        {
            try
            {
                var json = ReadString(dados, chave);
                return string.IsNullOrWhiteSpace(json)
                    ? new List<ConversationTopicState>()
                    : JsonSerializer.Deserialize<List<ConversationTopicState>>(json, JsonOptions) ?? new List<ConversationTopicState>();
            }
            catch
            {
                return new List<ConversationTopicState>();
            }
        }

        private static ConversationTopicSwitchPrompt? ReadSwitchPrompt(IReadOnlyDictionary<string, object>? dados)
        {
            try
            {
                var json = ReadString(dados, ConversationTopicContextKeys.SwitchPrompt);
                return string.IsNullOrWhiteSpace(json)
                    ? null
                    : JsonSerializer.Deserialize<ConversationTopicSwitchPrompt>(json, JsonOptions);
            }
            catch
            {
                return null;
            }
        }

        private static bool TryDeserializeFaq(string? json, out FaqTopicSnapshot faq)
        {
            try
            {
                faq = string.IsNullOrWhiteSpace(json)
                    ? new FaqTopicSnapshot()
                    : JsonSerializer.Deserialize<FaqTopicSnapshot>(json, JsonOptions) ?? new FaqTopicSnapshot();
                return faq.FaqId != Guid.Empty;
            }
            catch
            {
                faq = new FaqTopicSnapshot();
                return false;
            }
        }

        private static string? ReadString(IReadOnlyDictionary<string, object>? dados, string chave)
        {
            if (dados == null || !dados.TryGetValue(chave, out var valor) || valor == null)
            {
                return null;
            }

            return valor switch
            {
                string texto => texto,
                JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
                JsonElement json => json.ToString(),
                _ => valor.ToString()
            };
        }
    }
}
