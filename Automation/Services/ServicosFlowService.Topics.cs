using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using APIBack.Automation.Models;

namespace APIBack.Automation.Services
{
    public partial class ServicosFlowService
    {
        private static readonly JsonSerializerOptions TopicJsonOptions = new(JsonSerializerDefaults.Web);
        private static readonly Regex NumeroCurtoRegex = new(@"^\d{1,2}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public bool HasServiceTopicState(ConversationContext? contextoAtual, ServicoAtendimento? atendimentoAtual)
        {
            var estado = ResolveDeterministicState(contextoAtual?.Estado, atendimentoAtual?.EtapaAtual)
                        ?? contextoAtual?.Estado
                        ?? atendimentoAtual?.EtapaAtual;
            return IsServiceState(estado);
        }

        public bool ShouldBlockTopicSwitch(
            ConversationContext? contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            string mensagemTexto)
        {
            var estado = ResolveDeterministicState(contextoAtual?.Estado, atendimentoAtual?.EtapaAtual)
                        ?? contextoAtual?.Estado
                        ?? atendimentoAtual?.EtapaAtual;
            if (!IsServiceState(estado) || string.IsNullOrWhiteSpace(mensagemTexto))
            {
                return false;
            }

            var texto = mensagemTexto.Trim();
            if (NumeroCurtoRegex.IsMatch(texto) &&
                (string.Equals(estado, EstadoAguardandoEscolha, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(estado, EstadoAguardandoMarca, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if ((EhConfirmacaoPositiva(texto) || EhConfirmacaoNegativa(texto) || EhConfirmacaoFeliz(texto)) &&
                (string.Equals(estado, EstadoAguardandoConfirmacaoFinal, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(estado, EstadoAguardandoConfirmacaoHumano, StringComparison.OrdinalIgnoreCase) ||
                 TemTrocaPendente(contextoAtual, atendimentoAtual)))
            {
                return true;
            }

            if (EhContinuacaoGenerica(texto) &&
                (string.Equals(estado, EstadoOfertaDetalhes, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(estado, EstadoAguardandoMarca, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(estado, EstadoProntoAgendamento, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return false;
        }

        public ConversationTopicState? BuildActiveServiceTopic(
            ConversationContext? contextoAtual,
            ServicoAtendimento? atendimentoAtual)
        {
            var estado = ResolveDeterministicState(contextoAtual?.Estado, atendimentoAtual?.EtapaAtual)
                        ?? contextoAtual?.Estado
                        ?? atendimentoAtual?.EtapaAtual;
            if (!IsServiceState(estado))
            {
                return null;
            }

            var dados = ObterDadosServico(contextoAtual, atendimentoAtual);
            var servicoNome = ObterTexto(dados, ChaveServicoNome) ?? atendimentoAtual?.NomeServico ?? "servicos";
            var veiculoNome = ObterTexto(dados, ChaveVehicleNome) ?? ObterTexto(atendimentoAtual?.DadosExtras, ChaveVehicleNome);
            var marcaNome = ObterTexto(dados, ChaveMarcaPecaNome) ?? ObterTexto(atendimentoAtual?.DadosExtras, ChaveMarcaPecaNome);
            var resumo = BuildServiceTopicSummary(servicoNome, veiculoNome, marcaNome);

            var snapshot = new ServiceTopicSnapshot
            {
                Estado = estado,
                DadosColetados = CriarExtras(dados)
            };

            return new ConversationTopicState
            {
                TopicId = ObterTexto(contextoAtual?.DadosColetados, ConversationTopicContextKeys.Active) is { Length: > 0 } json &&
                          TryDeserializeTopic(json, out var existente)
                    ? existente.TopicId
                    : Guid.NewGuid().ToString("N"),
                Module = ConversationTopicModules.Servicos,
                Label = servicoNome,
                RuntimeStatus = MapRuntimeStatusFromService(estado, atendimentoAtual?.Status),
                Summary = resumo,
                SnapshotJson = JsonSerializer.Serialize(snapshot, TopicJsonOptions),
                UpdatedAt = DateTime.UtcNow
            };
        }

        public async Task<ConversationTopicState?> DetectDifferentServiceTopicAsync(
            Guid idEstabelecimento,
            string mensagemTexto,
            ConversationTopicState? activeTopic)
        {
            if (string.IsNullOrWhiteSpace(mensagemTexto) || NumeroCurtoRegex.IsMatch(mensagemTexto.Trim()))
            {
                return null;
            }

            var catalogo = await _catalogProvider.ObterCatalogoVisivelAsync(idEstabelecimento);
            var matches = SelecionarMelhoresCandidatos(MatchServices(mensagemTexto, catalogo));
            if (matches.Count != 1)
            {
                return null;
            }

            var servico = matches[0].Servico;
            if (TryGetTopicServiceId(activeTopic, out var activeServiceId) && activeServiceId == servico.Id)
            {
                return null;
            }

            return new ConversationTopicState
            {
                Module = ConversationTopicModules.Servicos,
                Label = servico.Nome,
                RuntimeStatus = ConversationTopicRuntimeStatus.Ativo,
                Summary = servico.Nome,
                PendingMessage = mensagemTexto.Trim(),
                MatchScore = matches[0].Score,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
        }

        public ConversationContext BuildRestoredServiceContext(
            ConversationContext? contextoAtual,
            ConversationTopicState activeTopic,
            IReadOnlyList<ConversationTopicState> queue)
        {
            if (!TryDeserializeServiceSnapshot(activeTopic.SnapshotJson, out var snapshot) || string.IsNullOrWhiteSpace(snapshot.Estado))
            {
                return new ConversationContext
                {
                    Estado = EstadoAguardandoServico,
                    DadosColetados = BuildTopicPayload(
                        new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),
                        activeTopic,
                        queue)
                };
            }

            var dados = snapshot.DadosColetados?
                .Where(item => item.Value != null)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            dados.Remove(ChaveAtendimentoId);
            var payload = BuildTopicPayload(dados, activeTopic, queue);

            return new ConversationContext
            {
                Estado = snapshot.Estado,
                DadosColetados = payload,
                ExpiracaoEstado = contextoAtual?.ExpiracaoEstado
            };
        }

        public string BuildServiceResumePrompt(ConversationTopicState topic)
        {
            if (!TryDeserializeServiceSnapshot(topic.SnapshotJson, out var snapshot))
            {
                return "Perfeito. Vamos continuar no assunto atual.";
            }

            var dados = snapshot.DadosColetados;
            var resumo = BuildServiceTopicSummary(
                ObterTexto(dados, ChaveServicoNome) ?? topic.Label,
                ObterTexto(dados, ChaveVehicleNome),
                ObterTexto(dados, ChaveMarcaPecaNome));

            return snapshot.Estado switch
            {
                EstadoAguardandoNome => "Antes de continuar, me fala seu nome.",
                EstadoAguardandoServico => "Me fala qual servico voce quer ver.",
                EstadoAguardandoVeiculo => string.IsNullOrWhiteSpace(resumo)
                    ? "Agora me fala a marca e o modelo do veiculo."
                    : $"{resumo}. Agora me fala a marca e o modelo do veiculo.",
                EstadoOfertaDetalhes => string.IsNullOrWhiteSpace(resumo)
                    ? "Se quiser, eu posso te passar valor, tempo ou marcas."
                    : $"{resumo}. Se quiser, eu posso te passar valor, tempo ou marcas.",
                EstadoAguardandoMarca => "Agora eu so preciso que voce escolha a marca. Me responde com o numero ou com o nome.",
                EstadoAguardandoConfirmacaoFinal => MontarResumoFinal(
                    cliente: null,
                    servicoNome: ObterTexto(dados, ChaveServicoNome) ?? topic.Label,
                    veiculoNome: ObterTexto(dados, ChaveVehicleNome),
                    marcaNome: ObterTexto(dados, ChaveMarcaPecaNome),
                    valorTexto: BuildServiceValueLineFromSnapshot(dados)),
                EstadoProntoAgendamento => "Perfeito. No proximo passo eu sigo com voce para o agendamento.",
                EstadoAguardandoConfirmacaoHumano => "Se quiser falar com a equipe, responde sim. Se preferir continuar por aqui, responde nao.",
                _ => string.IsNullOrWhiteSpace(resumo)
                    ? "Perfeito. Vamos continuar no assunto atual."
                    : $"{resumo}. Vamos continuar daqui."
            };
        }

        public static bool TryDeserializeTopic(string? json, out ConversationTopicState topic)
        {
            try
            {
                topic = string.IsNullOrWhiteSpace(json)
                    ? new ConversationTopicState()
                    : JsonSerializer.Deserialize<ConversationTopicState>(json, TopicJsonOptions) ?? new ConversationTopicState();
                return !string.IsNullOrWhiteSpace(json);
            }
            catch
            {
                topic = new ConversationTopicState();
                return false;
            }
        }

        private static bool TryDeserializeServiceSnapshot(string? json, out ServiceTopicSnapshot snapshot)
        {
            try
            {
                snapshot = string.IsNullOrWhiteSpace(json)
                    ? new ServiceTopicSnapshot()
                    : JsonSerializer.Deserialize<ServiceTopicSnapshot>(json, TopicJsonOptions) ?? new ServiceTopicSnapshot();
                return !string.IsNullOrWhiteSpace(snapshot.Estado);
            }
            catch
            {
                snapshot = new ServiceTopicSnapshot();
                return false;
            }
        }

        private static string MapRuntimeStatusFromService(string? estado, string? statusAtendimento)
        {
            if (string.Equals(statusAtendimento, "aguardando_interno", StringComparison.OrdinalIgnoreCase))
            {
                return ConversationTopicRuntimeStatus.AguardandoInterno;
            }

            if (string.Equals(statusAtendimento, "em_andamento", StringComparison.OrdinalIgnoreCase))
            {
                return ConversationTopicRuntimeStatus.AguardandoInterno;
            }

            return string.Equals(estado, EstadoProntoAgendamento, StringComparison.OrdinalIgnoreCase)
                ? ConversationTopicRuntimeStatus.AguardandoCliente
                : ConversationTopicRuntimeStatus.Ativo;
        }

        private static bool TryGetTopicServiceId(ConversationTopicState? topic, out Guid idServico)
        {
            idServico = Guid.Empty;
            if (topic == null || !TryDeserializeServiceSnapshot(topic.SnapshotJson, out var snapshot))
            {
                return false;
            }

            var value = ObterTexto(snapshot.DadosColetados, ChaveServicoId);
            return Guid.TryParse(value, out idServico);
        }

        private static Dictionary<string, object> BuildTopicPayload(
            Dictionary<string, object> dados,
            ConversationTopicState activeTopic,
            IReadOnlyList<ConversationTopicState> queue)
        {
            activeTopic.RuntimeStatus = ConversationTopicRuntimeStatus.Ativo;
            activeTopic.UpdatedAt = DateTime.UtcNow;

            dados[ConversationTopicContextKeys.Active] = JsonSerializer.Serialize(activeTopic, TopicJsonOptions);
            if (queue.Count > 0)
            {
                dados[ConversationTopicContextKeys.Queue] = JsonSerializer.Serialize(queue, TopicJsonOptions);
            }
            else
            {
                dados.Remove(ConversationTopicContextKeys.Queue);
            }

            dados.Remove(ConversationTopicContextKeys.Candidate);
            dados.Remove(ConversationTopicContextKeys.SwitchPrompt);
            return dados;
        }

        private static string BuildServiceTopicSummary(string? servicoNome, string? veiculoNome, string? marcaNome)
        {
            var partes = new List<string>();
            if (!string.IsNullOrWhiteSpace(servicoNome))
            {
                partes.Add(servicoNome!.Trim());
            }

            if (!string.IsNullOrWhiteSpace(veiculoNome))
            {
                partes.Add($"para {veiculoNome!.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(marcaNome))
            {
                partes.Add($"com a marca {marcaNome!.Trim()}");
            }

            return string.Join(" ", partes).Trim();
        }

        private static string? BuildServiceValueLineFromSnapshot(IReadOnlyDictionary<string, object?>? dados)
        {
            return ObterTexto(dados, ChaveValorLinha);
        }
    }
}
