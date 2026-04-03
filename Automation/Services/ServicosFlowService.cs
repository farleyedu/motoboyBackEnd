using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class ServicosFlowService
    {
        public const string EstadoAguardandoCategoria = "servicos_aguardando_categoria";
        public const string EstadoAguardandoEscolha = "servicos_aguardando_escolha";
        public const string EstadoAguardandoVeiculo = "servicos_aguardando_veiculo";
        public const string EstadoAguardandoConfirmacaoHumano = "servicos_aguardando_confirmacao_humano";

        private const string ChaveAtendimentoId = "servicos_atendimento_id";
        private const string ChaveCategoriaOpcoes = "servicos_categoria_opcoes";
        private const string ChaveCandidatosIds = "servicos_candidatos_ids";
        private const string ChaveServicoId = "servicos_servico_id";
        private const string ChaveTentativaSemMatch = "servicos_tentativa_sem_match";
        private const string ChaveUsuarioPerguntouPreco = "servicos_usuario_perguntou_preco";
        private const string ChaveUsuarioPerguntouDuracao = "servicos_usuario_perguntou_duracao";
        private const string ChaveVehiclePromptReason = "servicos_vehicle_prompt_reason";
        private const string ChaveUltimaSolicitacaoHumano = "servicos_ultima_solicitacao_humano";
        private const string ChaveVehicleOptions = "servicos_vehicle_options";
        private const string ChaveVehicleId = "servicos_vehicle_id";
        private const string ChaveVehicleNome = "servicos_vehicle_nome";

        private static readonly Regex EspacosRegex = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex TokenRegex = new(@"[a-z0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
        {
            "a", "ao", "aos", "as", "com", "da", "das", "de", "do", "dos", "e", "em", "na", "nas",
            "no", "nos", "o", "os", "ou", "para", "pra", "por", "que", "se", "servico", "servicos",
            "serviço", "serviços", "um", "uma"
        };

        private readonly IConversationRepository _conversationRepository;
        private readonly IClienteRepository _clienteRepository;
        private readonly IEstabelecimentoRepository _estabelecimentoRepository;
        private readonly IServicoAtendimentoRepository _servicoAtendimentoRepository;
        private readonly ServicoCatalogProvider _catalogProvider;
        private readonly ServicoReplyComposer _replyComposer;
        private readonly CentralRoutingService _centralRouting;
        private readonly ILogger<ServicosFlowService> _logger;

        public ServicosFlowService(
            IConversationRepository conversationRepository,
            IClienteRepository clienteRepository,
            IEstabelecimentoRepository estabelecimentoRepository,
            IServicoAtendimentoRepository servicoAtendimentoRepository,
            ServicoCatalogProvider catalogProvider,
            ServicoReplyComposer replyComposer,
            CentralRoutingService centralRouting,
            ILogger<ServicosFlowService> logger)
        {
            _conversationRepository = conversationRepository;
            _clienteRepository = clienteRepository;
            _estabelecimentoRepository = estabelecimentoRepository;
            _servicoAtendimentoRepository = servicoAtendimentoRepository;
            _catalogProvider = catalogProvider;
            _replyComposer = replyComposer;
            _centralRouting = centralRouting;
            _logger = logger;
        }

        public async Task<(bool Intercepted, AssistantDecision? Decision)> TryHandleAsync(
            Guid idConversa,
            string mensagemTexto,
            string? phoneNumberDisplay)
        {
            var scope = await _centralRouting.ResolveEffectiveScopeAsync(idConversa);
            if (scope == null || scope.IdEstabelecimento == Guid.Empty || scope.IdCliente == Guid.Empty)
            {
                return (false, null);
            }

            var tipoEstabelecimento = await _estabelecimentoRepository.ObterTipoEstabelecimentoAsync(scope.IdEstabelecimento);
            if (string.Equals(tipoEstabelecimento, "garagem", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tipoEstabelecimento, "nautica", StringComparison.OrdinalIgnoreCase))
            {
                return (false, null);
            }

            var modulosAtivos = await _estabelecimentoRepository.ObterModulosAtivosAsync(scope.IdEstabelecimento);
            if (!modulosAtivos.Any(item => string.Equals(item, "Servicos", StringComparison.OrdinalIgnoreCase)))
            {
                return (false, null);
            }

            var contextoAtual = await _conversationRepository.ObterContextoAsync(idConversa);
            if (TemContextoDeOutroFluxo(contextoAtual?.Estado))
            {
                return (false, null);
            }

            var cliente = await _clienteRepository.ObterPorIdAsync(scope.IdCliente);
            if (string.Equals(tipoEstabelecimento, "oficina", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(cliente?.Nome) &&
                !IsServiceState(contextoAtual?.Estado))
            {
                return (false, null);
            }

            var catalogo = await _catalogProvider.ObterCatalogoVisivelAsync(scope.IdEstabelecimento);
            if (catalogo.Count == 0)
            {
                return (false, null);
            }

            var atendimentoAtual = await ObterAtendimentoAtualAsync(idConversa, scope);
            var texto = mensagemTexto?.Trim() ?? string.Empty;
            var viaNumeroCentral = _centralRouting.IsCentralDisplayPhone(phoneNumberDisplay);

            if (IsServiceState(contextoAtual?.Estado))
            {
                if (atendimentoAtual != null && IsOpenStatus(atendimentoAtual.Status))
                {
                    atendimentoAtual.Status = "em_triagem";
                    atendimentoAtual.EtapaAtual = "triagem";
                    await _servicoAtendimentoRepository.AtualizarAsync(atendimentoAtual);
                }

                var decisaoEstado = await ProcessarEstadoAsync(
                    idConversa,
                    scope,
                    cliente,
                    catalogo,
                    contextoAtual!,
                    atendimentoAtual,
                    texto,
                    viaNumeroCentral);

                return (true, decisaoEstado);
            }

            if (atendimentoAtual != null &&
                atendimentoAtual.IdServico.HasValue &&
                TryObterServicoPorId(catalogo, atendimentoAtual.IdServico.Value, out var servicoAtual) &&
                PodeContinuarServicoAtual(texto, atendimentoAtual))
            {
                var priceAsked = UsuarioPerguntouPreco(texto) || ObterBool(atendimentoAtual.DadosExtras, ChaveUsuarioPerguntouPreco);
                var durationAsked = UsuarioPerguntouDuracao(texto) || ObterBool(atendimentoAtual.DadosExtras, ChaveUsuarioPerguntouDuracao);
                var vehicle = ObterVeiculoAtual(atendimentoAtual, servicoAtual);

                var decisaoServicoAtual = await ResponderServicoSelecionadoAsync(
                    idConversa,
                    scope,
                    cliente,
                    atendimentoAtual,
                    contextoAtual,
                    servicoAtual,
                    texto,
                    priceAsked,
                    durationAsked,
                    vehicle,
                    viaNumeroCentral);

                return (true, decisaoServicoAtual);
            }

            if (atendimentoAtual != null && EhMensagemEncerramento(texto))
            {
                await _servicoAtendimentoRepository.ConcluirAsync(atendimentoAtual.Id, "concluido");
                await LimparContextoPreservandoSelecaoAsync(idConversa);
                return (true, new AssistantDecision("Perfeito. Se precisar de mais algum servico, me chama por aqui.", "none", null, false, null));
            }

            if (!DeveInterceptarNovaMensagem(texto, catalogo))
            {
                return (false, null);
            }

            var decisao = await ProcessarNovaMensagemAsync(
                idConversa,
                scope,
                cliente,
                catalogo,
                atendimentoAtual,
                contextoAtual,
                texto,
                viaNumeroCentral);

            return (true, decisao);
        }

        private async Task<AssistantDecision> ProcessarEstadoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            IReadOnlyList<ServicoCatalogItem> catalogo,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            string mensagemTexto,
            bool viaNumeroCentral)
        {
            var estado = contextoAtual.Estado ?? string.Empty;
            if (string.Equals(estado, EstadoAguardandoCategoria, StringComparison.OrdinalIgnoreCase))
            {
                return await ProcessarEscolhaCategoriaAsync(
                    idConversa,
                    scope,
                    cliente,
                    catalogo,
                    contextoAtual,
                    atendimentoAtual,
                    mensagemTexto,
                    viaNumeroCentral);
            }

            if (string.Equals(estado, EstadoAguardandoEscolha, StringComparison.OrdinalIgnoreCase))
            {
                return await ProcessarEscolhaServicoAsync(
                    idConversa,
                    scope,
                    cliente,
                    catalogo,
                    contextoAtual,
                    atendimentoAtual,
                    mensagemTexto,
                    viaNumeroCentral);
            }

            if (string.Equals(estado, EstadoAguardandoVeiculo, StringComparison.OrdinalIgnoreCase))
            {
                return await ProcessarEscolhaVeiculoAsync(
                    idConversa,
                    scope,
                    cliente,
                    catalogo,
                    contextoAtual,
                    atendimentoAtual,
                    mensagemTexto,
                    viaNumeroCentral);
            }

            return await ProcessarConfirmacaoHumanoAsync(
                idConversa,
                scope,
                cliente,
                catalogo,
                contextoAtual,
                atendimentoAtual,
                mensagemTexto,
                viaNumeroCentral);
        }

        private async Task<AssistantDecision> ProcessarNovaMensagemAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            IReadOnlyList<ServicoCatalogItem> catalogo,
            ServicoAtendimento? atendimentoAtual,
            ConversationContext? contextoAtual,
            string mensagemTexto,
            bool viaNumeroCentral)
        {
            var priceAsked = UsuarioPerguntouPreco(mensagemTexto);
            var durationAsked = UsuarioPerguntouDuracao(mensagemTexto);

            if (FoiPedidoHumano(mensagemTexto))
            {
                var atendimentoHandover = await ObterOuCriarAtendimentoAsync(
                    idConversa,
                    scope,
                    cliente,
                    atendimentoAtual,
                    intencaoPrincipal: "falar_com_humano",
                    intencaoDetalhe: "pedido_explicito",
                    servico: null,
                    status: "aguardando_interno",
                    etapaAtual: "encaminhado",
                    ultimaPergunta: null,
                    viaNumeroCentral,
                    CriarExtras(new Dictionary<string, object?>
                    {
                        [ChaveUltimaSolicitacaoHumano] = mensagemTexto
                    }));

                await LimparContextoPreservandoSelecaoAsync(idConversa);
                return await CriarDecisionHandoverAsync(idConversa, scope, cliente, atendimentoHandover, mensagemTexto);
            }

            if (EhPerguntaAmplaDeServicos(mensagemTexto))
            {
                return await PerguntarCategoriaAsync(
                    idConversa,
                    scope,
                    cliente,
                    atendimentoAtual,
                    contextoAtual,
                    catalogo,
                    mensagemTexto,
                    viaNumeroCentral);
            }

            var matches = MatchServices(mensagemTexto, catalogo);
            if (matches.Count == 0)
            {
                return await TratarSemMatchAsync(
                    idConversa,
                    scope,
                    cliente,
                    atendimentoAtual,
                    contextoAtual,
                    catalogo,
                    mensagemTexto,
                    viaNumeroCentral);
            }

            var melhores = SelecionarMelhoresCandidatos(matches);
            if (melhores.Count == 1)
            {
                return await ResponderServicoSelecionadoAsync(
                    idConversa,
                    scope,
                    cliente,
                    atendimentoAtual,
                    contextoAtual,
                    melhores[0].Servico,
                    mensagemTexto,
                    priceAsked,
                    durationAsked,
                    null,
                    viaNumeroCentral);
            }

            if (melhores.Count <= 3)
            {
                return await PerguntarEscolhaServicoAsync(
                    idConversa,
                    scope,
                    cliente,
                    atendimentoAtual,
                    contextoAtual,
                    melhores.Select(item => item.Servico).ToArray(),
                    mensagemTexto,
                    priceAsked,
                    durationAsked,
                    viaNumeroCentral);
            }

            return await PerguntarCategoriaAsync(
                idConversa,
                scope,
                cliente,
                atendimentoAtual,
                contextoAtual,
                catalogo,
                mensagemTexto,
                viaNumeroCentral);
        }

        private async Task<AssistantDecision> ProcessarEscolhaCategoriaAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            IReadOnlyList<ServicoCatalogItem> catalogo,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            string mensagemTexto,
            bool viaNumeroCentral)
        {
            var categorias = ObterListaTexto(contextoAtual.DadosColetados, ChaveCategoriaOpcoes);
            var categoria = ResolverOpcaoPorTextoOuIndice(mensagemTexto, categorias, "servicos_cat_");
            if (string.IsNullOrWhiteSpace(categoria))
            {
                return await TratarSemMatchAsync(
                    idConversa,
                    scope,
                    cliente,
                    atendimentoAtual,
                    contextoAtual,
                    catalogo,
                    mensagemTexto,
                    viaNumeroCentral,
                    "Nao consegui identificar a categoria.");
            }

            var filtrados = catalogo
                .Where(item => string.Equals(NormalizeText(item.Tipo), NormalizeText(categoria), StringComparison.Ordinal))
                .ToArray();

            if (filtrados.Length == 0)
            {
                return await TratarSemMatchAsync(
                    idConversa,
                    scope,
                    cliente,
                    atendimentoAtual,
                    contextoAtual,
                    catalogo,
                    mensagemTexto,
                    viaNumeroCentral,
                    "Nao encontrei servicos ativos nessa categoria.");
            }

            if (filtrados.Length == 1)
            {
                return await ResponderServicoSelecionadoAsync(
                    idConversa,
                    scope,
                    cliente,
                    atendimentoAtual,
                    contextoAtual,
                    filtrados[0],
                    mensagemTexto,
                    priceAsked: false,
                    durationAsked: false,
                    vehicle: null,
                    viaNumeroCentral);
            }

            var sugeridos = filtrados.Take(3).ToArray();
            return await PerguntarEscolhaServicoAsync(
                idConversa,
                scope,
                cliente,
                atendimentoAtual,
                contextoAtual,
                sugeridos,
                $"categoria:{categoria}",
                priceAsked: false,
                durationAsked: false,
                viaNumeroCentral);
        }

        private async Task<AssistantDecision> ProcessarEscolhaServicoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            IReadOnlyList<ServicoCatalogItem> catalogo,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            string mensagemTexto,
            bool viaNumeroCentral)
        {
            var candidatosIds = ObterListaTexto(contextoAtual.DadosColetados, ChaveCandidatosIds);
            var candidatos = candidatosIds
                .Select(id => Guid.TryParse(id, out var parsed) ? parsed : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Select(id => catalogo.FirstOrDefault(item => item.Id == id))
                .Where(item => item != null)
                .Cast<ServicoCatalogItem>()
                .ToArray();

            var servico = ResolverServicoSelecionado(mensagemTexto, candidatos);
            if (servico == null)
            {
                return await TratarSemMatchAsync(
                    idConversa,
                    scope,
                    cliente,
                    atendimentoAtual,
                    contextoAtual,
                    catalogo,
                    mensagemTexto,
                    viaNumeroCentral,
                    "Nao consegui identificar qual servico voce escolheu.");
            }

            var priceAsked = ObterBool(contextoAtual.DadosColetados, ChaveUsuarioPerguntouPreco);
            var durationAsked = ObterBool(contextoAtual.DadosColetados, ChaveUsuarioPerguntouDuracao);

            return await ResponderServicoSelecionadoAsync(
                idConversa,
                scope,
                cliente,
                atendimentoAtual,
                contextoAtual,
                servico,
                mensagemTexto,
                priceAsked,
                durationAsked,
                null,
                viaNumeroCentral);
        }

        private async Task<AssistantDecision> ProcessarEscolhaVeiculoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            IReadOnlyList<ServicoCatalogItem> catalogo,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            string mensagemTexto,
            bool viaNumeroCentral)
        {
            var servico = ResolverServicoDoContextoOuAtendimento(contextoAtual, atendimentoAtual, catalogo);
            if (servico == null)
            {
                return await TratarSemMatchAsync(
                    idConversa,
                    scope,
                    cliente,
                    atendimentoAtual,
                    contextoAtual,
                    catalogo,
                    mensagemTexto,
                    viaNumeroCentral,
                    "Perdi o contexto do servico.");
            }

            var vehicle = ResolverVeiculoSelecionado(mensagemTexto, servico, contextoAtual);
            if (vehicle == null)
            {
                return await TratarSemMatchAsync(
                    idConversa,
                    scope,
                    cliente,
                    atendimentoAtual,
                    contextoAtual,
                    catalogo,
                    mensagemTexto,
                    viaNumeroCentral,
                    "Nao consegui identificar o veiculo.");
            }

            var priceAsked = ObterBool(contextoAtual.DadosColetados, ChaveUsuarioPerguntouPreco);
            var durationAsked = ObterBool(contextoAtual.DadosColetados, ChaveUsuarioPerguntouDuracao);

            return await ResponderServicoSelecionadoAsync(
                idConversa,
                scope,
                cliente,
                atendimentoAtual,
                contextoAtual,
                servico,
                mensagemTexto,
                priceAsked,
                durationAsked,
                vehicle,
                viaNumeroCentral);
        }

        private async Task<AssistantDecision> ProcessarConfirmacaoHumanoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            IReadOnlyList<ServicoCatalogItem> catalogo,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            string mensagemTexto,
            bool viaNumeroCentral)
        {
            if (EhConfirmacaoPositiva(mensagemTexto))
            {
                var servico = ResolverServicoDoContextoOuAtendimento(contextoAtual, atendimentoAtual, catalogo);
                var atendimentoHandover = await ObterOuCriarAtendimentoAsync(
                    idConversa,
                    scope,
                    cliente,
                    atendimentoAtual,
                    intencaoPrincipal: "falar_com_humano",
                    intencaoDetalhe: "sem_match_servicos",
                    servico,
                    status: "aguardando_interno",
                    etapaAtual: "encaminhado",
                    ultimaPergunta: null,
                    viaNumeroCentral,
                    CriarExtras(new Dictionary<string, object?>
                    {
                        [ChaveUltimaSolicitacaoHumano] = ObterTexto(contextoAtual.DadosColetados, ChaveUltimaSolicitacaoHumano) ?? mensagemTexto
                    }));

                await LimparContextoPreservandoSelecaoAsync(idConversa);
                return await CriarDecisionHandoverAsync(
                    idConversa,
                    scope,
                    cliente,
                    atendimentoHandover,
                    ObterTexto(contextoAtual.DadosColetados, ChaveUltimaSolicitacaoHumano) ?? mensagemTexto);
            }

            if (EhConfirmacaoNegativa(mensagemTexto))
            {
                if (atendimentoAtual != null)
                {
                    await _servicoAtendimentoRepository.ConcluirAsync(atendimentoAtual.Id, "cancelado");
                }

                await LimparContextoPreservandoSelecaoAsync(idConversa);
                return new AssistantDecision(
                    "Sem problema. Se quiser, me fala o servico ou a categoria que eu continuo te ajudando por aqui.",
                    "none",
                    null,
                    false,
                    null);
            }

            var reply = "Se quiser falar com a equipe, responde sim. Se preferir continuar por aqui, responde nao.";
            var atendimento = await ObterOuCriarAtendimentoAsync(
                idConversa,
                scope,
                cliente,
                atendimentoAtual,
                intencaoPrincipal: "falar_com_humano",
                intencaoDetalhe: "confirmacao_pendente",
                servico: null,
                status: "aguardando_cliente",
                etapaAtual: "confirmacao_humano",
                ultimaPergunta: reply,
                viaNumeroCentral,
                CriarExtras(new Dictionary<string, object?>()));

            await SalvarContextoServicoAsync(
                idConversa,
                contextoAtual,
                EstadoAguardandoConfirmacaoHumano,
                atendimento?.Id,
                new Dictionary<string, object?>
                {
                    [ChaveUltimaSolicitacaoHumano] = ObterTexto(contextoAtual.DadosColetados, ChaveUltimaSolicitacaoHumano) ?? mensagemTexto
                });

            return new AssistantDecision(reply, "none", null, false, null, null, BuildSimNaoButtons());
        }

        private async Task<AssistantDecision> ResponderServicoSelecionadoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            ServicoAtendimento? atendimentoAtual,
            ConversationContext? contextoAtual,
            ServicoCatalogItem servico,
            string mensagemTexto,
            bool priceAsked,
            bool durationAsked,
            ServicoCatalogVehicleItem? vehicle,
            bool viaNumeroCentral)
        {
            if (servico.DiferePorVeiculo && vehicle == null)
            {
                vehicle = ObterVeiculoAtual(atendimentoAtual, servico);
            }

            if (servico.DiferePorVeiculo && vehicle == null)
            {
                return await PerguntarVeiculoAsync(
                    idConversa,
                    scope,
                    cliente,
                    atendimentoAtual,
                    contextoAtual,
                    servico,
                    priceAsked,
                    durationAsked,
                    viaNumeroCentral);
            }

            if (vehicle != null && !vehicle.Compativel)
            {
                var replyIncompativel = $"Nao encontrei compatibilidade segura de {servico.Nome} para {vehicle.NomeExibicao}. Se quiser, eu passo para a equipe confirmar isso com voce.";
                return await PerguntarConfirmacaoHumanoAsync(
                    idConversa,
                    scope,
                    cliente,
                    atendimentoAtual,
                    contextoAtual,
                    servico,
                    replyIncompativel,
                    mensagemTexto,
                    viaNumeroCentral);
            }

            var summary = BuildServiceSummary(servico);
            var detalhes = new List<string>();

            if (priceAsked)
            {
                var textoPreco = BuildPriceText(servico, vehicle);
                if (textoPreco == null)
                {
                    var semPreco = $"Eu nao tenho o valor configurado de {servico.Nome} no catalogo agora. Se quiser, eu passo isso para a equipe.";
                    return await PerguntarConfirmacaoHumanoAsync(
                        idConversa,
                        scope,
                        cliente,
                        atendimentoAtual,
                        contextoAtual,
                        servico,
                        semPreco,
                        mensagemTexto,
                        viaNumeroCentral);
                }

                detalhes.Add(textoPreco);
            }

            if (durationAsked)
            {
                detalhes.Add($"Tempo estimado: {FormatDuration(servico.DuracaoMinutos)}.");
            }

            string fallbackReply;
            string factsPrompt;
            string statusFinal;
            string? proximaPergunta = null;
            string etapaAtual;

            if (!priceAsked && !durationAsked)
            {
                if (servico.DiferePorVeiculo && vehicle != null)
                {
                    detalhes.Add($"Para {vehicle.NomeExibicao}, eu consigo te orientar melhor sobre valor e compatibilidade.");
                }

                proximaPergunta = servico.PermiteAgendamento
                    ? "Se quiser, eu posso te explicar valor, tempo ou como esse servico costuma funcionar."
                    : "Se quiser, eu posso te explicar valor, tempo ou mais detalhes desse servico.";

                fallbackReply = $"{summary} {proximaPergunta}".Trim();
                factsPrompt = BuildFactsPrompt(summary, detalhes, proximaPergunta);
                statusFinal = "aguardando_cliente";
                etapaAtual = "aguardando_cliente";
            }
            else
            {
                fallbackReply = $"{summary} {string.Join(" ", detalhes)}".Trim();
                factsPrompt = BuildFactsPrompt(summary, detalhes, null);
                statusFinal = "concluido";
                etapaAtual = "respondido";
            }

            var reply = await _replyComposer.ComposeAsync(idConversa, factsPrompt, fallbackReply);

            var extras = new Dictionary<string, object?>
            {
                [ChaveUsuarioPerguntouPreco] = priceAsked,
                [ChaveUsuarioPerguntouDuracao] = durationAsked
            };

            if (vehicle != null)
            {
                extras[ChaveVehicleId] = vehicle.CarroId.ToString();
                extras[ChaveVehicleNome] = vehicle.NomeExibicao;
            }

            var atendimento = await ObterOuCriarAtendimentoAsync(
                idConversa,
                scope,
                cliente,
                atendimentoAtual,
                intencaoPrincipal: priceAsked ? "preco_servico" : durationAsked ? "duracao_servico" : "duvida_servicos",
                intencaoDetalhe: servico.Tipo,
                servico,
                status: statusFinal,
                etapaAtual,
                ultimaPergunta: proximaPergunta,
                viaNumeroCentral,
                CriarExtras(extras));

            await LimparContextoPreservandoSelecaoAsync(idConversa);

            if (atendimento != null && string.Equals(statusFinal, "concluido", StringComparison.OrdinalIgnoreCase))
            {
                await _servicoAtendimentoRepository.ConcluirAsync(atendimento.Id, "concluido");
            }

            return new AssistantDecision(reply, "none", null, false, null);
        }

        private async Task<AssistantDecision> PerguntarCategoriaAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            ServicoAtendimento? atendimentoAtual,
            ConversationContext? contextoAtual,
            IReadOnlyList<ServicoCatalogItem> catalogo,
            string mensagemTexto,
            bool viaNumeroCentral)
        {
            var categorias = catalogo
                .Where(item => !string.IsNullOrWhiteSpace(item.Tipo))
                .GroupBy(item => item.Tipo.Trim(), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key)
                .Select(group => group.Key)
                .Take(3)
                .ToArray();

            if (categorias.Length == 0)
            {
                categorias = catalogo.Select(item => item.Nome).Take(3).ToArray();
            }

            var pergunta = categorias.Length switch
            {
                1 => $"Hoje eu consigo te ajudar com {categorias[0]}. Quer seguir por esse caminho?",
                2 => $"Para eu te orientar melhor, qual categoria faz mais sentido agora: {categorias[0]} ou {categorias[1]}?",
                _ => $"Para eu te orientar melhor, voce quer olhar {categorias[0]}, {categorias[1]} ou {categorias[2]}?"
            };

            var atendimento = await ObterOuCriarAtendimentoAsync(
                idConversa,
                scope,
                cliente,
                atendimentoAtual,
                intencaoPrincipal: "catalogo_servicos",
                intencaoDetalhe: "categoria",
                servico: null,
                status: "aguardando_cliente",
                etapaAtual: "aguardando_categoria",
                ultimaPergunta: pergunta,
                viaNumeroCentral,
                CriarExtras(new Dictionary<string, object?>
                {
                    [ChaveTentativaSemMatch] = 0
                }));

            await SalvarContextoServicoAsync(
                idConversa,
                contextoAtual,
                EstadoAguardandoCategoria,
                atendimento?.Id,
                new Dictionary<string, object?>
                {
                    [ChaveCategoriaOpcoes] = categorias,
                    [ChaveTentativaSemMatch] = 0
                });

            return new AssistantDecision(pergunta, "none", null, false, null, null, BuildIndexedButtons("servicos_cat_", categorias));
        }

        private async Task<AssistantDecision> PerguntarEscolhaServicoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            ServicoAtendimento? atendimentoAtual,
            ConversationContext? contextoAtual,
            IReadOnlyList<ServicoCatalogItem> candidatos,
            string mensagemOrigem,
            bool priceAsked,
            bool durationAsked,
            bool viaNumeroCentral)
        {
            var nomes = candidatos.Select(item => item.Nome).ToArray();
            var pergunta = nomes.Length switch
            {
                1 => $"Voce esta falando de {nomes[0]}?",
                2 => $"Entendi. Voce quer {nomes[0]} ou {nomes[1]}?",
                _ => $"Entendi. Qual desses servicos faz mais sentido: {nomes[0]}, {nomes[1]} ou {nomes[2]}?"
            };

            var atendimento = await ObterOuCriarAtendimentoAsync(
                idConversa,
                scope,
                cliente,
                atendimentoAtual,
                intencaoPrincipal: "duvida_servicos",
                intencaoDetalhe: "escolha_servico",
                servico: null,
                status: "aguardando_cliente",
                etapaAtual: "aguardando_escolha",
                ultimaPergunta: pergunta,
                viaNumeroCentral,
                CriarExtras(new Dictionary<string, object?>
                {
                    [ChaveUsuarioPerguntouPreco] = priceAsked,
                    [ChaveUsuarioPerguntouDuracao] = durationAsked,
                    [ChaveTentativaSemMatch] = 0
                }));

            await SalvarContextoServicoAsync(
                idConversa,
                contextoAtual,
                EstadoAguardandoEscolha,
                atendimento?.Id,
                new Dictionary<string, object?>
                {
                    [ChaveCandidatosIds] = candidatos.Select(item => item.Id.ToString()).ToArray(),
                    [ChaveUsuarioPerguntouPreco] = priceAsked,
                    [ChaveUsuarioPerguntouDuracao] = durationAsked,
                    [ChaveTentativaSemMatch] = 0
                });

            return new AssistantDecision(pergunta, "none", null, false, null, null, BuildIndexedButtons("servicos_sel_", nomes));
        }

        private async Task<AssistantDecision> PerguntarVeiculoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            ServicoAtendimento? atendimentoAtual,
            ConversationContext? contextoAtual,
            ServicoCatalogItem servico,
            bool priceAsked,
            bool durationAsked,
            bool viaNumeroCentral)
        {
            var options = servico.Veiculos
                .Where(item => !string.IsNullOrWhiteSpace(item.NomeExibicao))
                .Select(item => item.NomeExibicao)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToArray();

            var pergunta = options.Length switch
            {
                0 => $"Para eu te orientar melhor sobre {servico.Nome}, me fala a marca e o modelo do veiculo.",
                1 => $"Para eu te orientar melhor sobre {servico.Nome}, seu veiculo e {options[0]}?",
                _ => $"Para eu te orientar melhor sobre {servico.Nome}, qual e o veiculo? Posso conferir por aqui, por exemplo: {string.Join(", ", options)}."
            };

            var atendimento = await ObterOuCriarAtendimentoAsync(
                idConversa,
                scope,
                cliente,
                atendimentoAtual,
                intencaoPrincipal: priceAsked ? "preco_servico" : "duvida_servicos",
                intencaoDetalhe: servico.Tipo,
                servico,
                status: "aguardando_cliente",
                etapaAtual: "aguardando_veiculo",
                ultimaPergunta: pergunta,
                viaNumeroCentral,
                CriarExtras(new Dictionary<string, object?>
                {
                    [ChaveUsuarioPerguntouPreco] = priceAsked,
                    [ChaveUsuarioPerguntouDuracao] = durationAsked,
                    [ChaveVehiclePromptReason] = priceAsked ? "preco" : "detalhes"
                }));

            await SalvarContextoServicoAsync(
                idConversa,
                contextoAtual,
                EstadoAguardandoVeiculo,
                atendimento?.Id,
                new Dictionary<string, object?>
                {
                    [ChaveServicoId] = servico.Id.ToString(),
                    [ChaveUsuarioPerguntouPreco] = priceAsked,
                    [ChaveUsuarioPerguntouDuracao] = durationAsked,
                    [ChaveVehiclePromptReason] = priceAsked ? "preco" : "detalhes",
                    [ChaveVehicleOptions] = options
                });

            var buttons = options.Length is > 1 and <= 3
                ? BuildIndexedButtons("servicos_veh_", options)
                : null;

            return new AssistantDecision(pergunta, "none", null, false, null, null, buttons);
        }

        private async Task<AssistantDecision> PerguntarConfirmacaoHumanoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            ServicoAtendimento? atendimentoAtual,
            ConversationContext? contextoAtual,
            ServicoCatalogItem? servico,
            string mensagemBase,
            string mensagemOrigem,
            bool viaNumeroCentral)
        {
            var reply = $"{mensagemBase} Quer que eu passe isso para a equipe?";
            var atendimento = await ObterOuCriarAtendimentoAsync(
                idConversa,
                scope,
                cliente,
                atendimentoAtual,
                intencaoPrincipal: "falar_com_humano",
                intencaoDetalhe: servico == null ? "confirmacao_humano" : "servico_sem_preco",
                servico,
                status: "aguardando_cliente",
                etapaAtual: "confirmacao_humano",
                ultimaPergunta: reply,
                viaNumeroCentral,
                CriarExtras(new Dictionary<string, object?>
                {
                    [ChaveUltimaSolicitacaoHumano] = mensagemOrigem
                }));

            await SalvarContextoServicoAsync(
                idConversa,
                contextoAtual,
                EstadoAguardandoConfirmacaoHumano,
                atendimento?.Id,
                new Dictionary<string, object?>
                {
                    [ChaveServicoId] = servico?.Id.ToString(),
                    [ChaveUltimaSolicitacaoHumano] = mensagemOrigem
                });

            return new AssistantDecision(reply, "none", null, false, null, null, BuildSimNaoButtons());
        }

        private async Task<AssistantDecision> TratarSemMatchAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            ServicoAtendimento? atendimentoAtual,
            ConversationContext? contextoAtual,
            IReadOnlyList<ServicoCatalogItem> catalogo,
            string mensagemTexto,
            bool viaNumeroCentral,
            string? prefixo = null)
        {
            var tentativaAtual = ObterInt(contextoAtual?.DadosColetados, ChaveTentativaSemMatch);
            if (tentativaAtual <= 0)
            {
                var pergunta = string.IsNullOrWhiteSpace(prefixo)
                    ? "Nao consegui identificar o servico ainda. Me fala o nome do servico ou a categoria principal."
                    : $"{prefixo} Me fala o nome do servico ou a categoria principal.";

                var atendimento = await ObterOuCriarAtendimentoAsync(
                    idConversa,
                    scope,
                    cliente,
                    atendimentoAtual,
                    intencaoPrincipal: "catalogo_servicos",
                    intencaoDetalhe: "clarificacao",
                    servico: null,
                    status: "aguardando_cliente",
                    etapaAtual: "aguardando_categoria",
                    ultimaPergunta: pergunta,
                    viaNumeroCentral,
                    CriarExtras(new Dictionary<string, object?>
                    {
                        [ChaveTentativaSemMatch] = 1
                    }));

                var categorias = catalogo
                    .Where(item => !string.IsNullOrWhiteSpace(item.Tipo))
                    .GroupBy(item => item.Tipo.Trim(), StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(group => group.Count())
                    .ThenBy(group => group.Key)
                    .Select(group => group.Key)
                    .Take(3)
                    .ToArray();

                await SalvarContextoServicoAsync(
                    idConversa,
                    contextoAtual,
                    EstadoAguardandoCategoria,
                    atendimento?.Id,
                    new Dictionary<string, object?>
                    {
                        [ChaveCategoriaOpcoes] = categorias,
                        [ChaveTentativaSemMatch] = 1
                    });

                var buttons = categorias.Length is > 1 and <= 3
                    ? BuildIndexedButtons("servicos_cat_", categorias)
                    : null;

                return new AssistantDecision(pergunta, "none", null, false, null, null, buttons);
            }

            return await PerguntarConfirmacaoHumanoAsync(
                idConversa,
                scope,
                cliente,
                atendimentoAtual,
                contextoAtual,
                servico: null,
                mensagemBase: "Ainda nao consegui fechar o servico com seguranca por aqui.",
                mensagemOrigem: mensagemTexto,
                viaNumeroCentral);
        }

        private async Task<AssistantDecision> CriarDecisionHandoverAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            ServicoAtendimento? atendimento,
            string motivo)
        {
            var telefone = await GarantirTelefoneClienteAsync(idConversa, scope);
            var detalhes = new HandoverContextDto
            {
                ClienteNome = cliente?.Nome,
                Telefone = telefone,
                Motivo = atendimento?.ResumoAtendimento ?? motivo,
                QueixaPrincipal = motivo,
                Contexto = atendimento?.NomeServico == null
                    ? "fluxo=servicos"
                    : $"fluxo=servicos; servico={atendimento.NomeServico}",
                Historico = atendimento == null
                    ? Array.Empty<string>()
                    : new[]
                    {
                        $"Atendimento servicos: {atendimento.Id}",
                        $"Servico: {atendimento.NomeServico ?? "nao definido"}",
                        $"Resumo: {atendimento.ResumoAtendimento ?? motivo}"
                    }
            };

            return new AssistantDecision(
                "Perfeito. Nossa equipe vai continuar esse atendimento por aqui.",
                "escalar_para_humano",
                null,
                false,
                detalhes);
        }

        private async Task<ServicoAtendimento?> ObterAtendimentoAtualAsync(Guid idConversa, EffectiveConversationScope scope)
        {
            var porConversa = await _servicoAtendimentoRepository.ObterPorConversaAsync(idConversa);
            if (porConversa != null && IsOpenStatus(porConversa.Status))
            {
                return porConversa;
            }

            var telefone = await GarantirTelefoneClienteAsync(idConversa, scope);
            if (string.IsNullOrWhiteSpace(telefone))
            {
                return null;
            }

            return await _servicoAtendimentoRepository.ObterAbertoAsync(scope.IdEstabelecimento, telefone);
        }

        private async Task<ServicoAtendimento?> ObterOuCriarAtendimentoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            ServicoAtendimento? atendimentoAtual,
            string intencaoPrincipal,
            string? intencaoDetalhe,
            ServicoCatalogItem? servico,
            string status,
            string etapaAtual,
            string? ultimaPergunta,
            bool viaNumeroCentral,
            IReadOnlyDictionary<string, object?> extras)
        {
            var telefone = await GarantirTelefoneClienteAsync(idConversa, scope) ?? string.Empty;
            var atendimento = atendimentoAtual;
            if (atendimento == null || !IsOpenStatus(atendimento.Status))
            {
                atendimento = await _servicoAtendimentoRepository.ObterPorConversaAsync(idConversa);
            }

            if (atendimento == null || !IsOpenStatus(atendimento.Status))
            {
                atendimento = string.IsNullOrWhiteSpace(telefone)
                    ? null
                    : await _servicoAtendimentoRepository.ObterAbertoAsync(scope.IdEstabelecimento, telefone);
            }

            if (atendimento == null || !IsOpenStatus(atendimento.Status))
            {
                atendimento = new ServicoAtendimento
                {
                    Id = Guid.NewGuid(),
                    IdEstabelecimento = scope.IdEstabelecimento,
                    IdConversa = idConversa,
                    IdCliente = scope.IdCliente,
                    TelefoneE164 = telefone,
                    NomeCliente = cliente?.Nome?.Trim(),
                    IntencaoPrincipal = intencaoPrincipal,
                    IntencaoDetalhe = intencaoDetalhe,
                    IdServico = servico?.Id,
                    NomeServico = servico?.Nome,
                    CategoriaServico = servico?.Tipo,
                    ResumoAtendimento = BuildResumoAtendimento(cliente?.Nome, servico, intencaoPrincipal, status),
                    Status = status,
                    EtapaAtual = etapaAtual,
                    UltimaPergunta = ultimaPergunta,
                    ViaNumeroCentral = viaNumeroCentral,
                    DadosExtras = new Dictionary<string, object?>(extras, StringComparer.OrdinalIgnoreCase),
                    DataHandover = string.Equals(status, "aguardando_interno", StringComparison.OrdinalIgnoreCase) ? DateTime.UtcNow : null,
                    DataConclusao = string.Equals(status, "concluido", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "cancelado", StringComparison.OrdinalIgnoreCase)
                        ? DateTime.UtcNow
                        : null,
                    DataCriacao = DateTime.UtcNow,
                    DataAtualizacao = DateTime.UtcNow
                };

                await _servicoAtendimentoRepository.CriarAsync(atendimento);
                return atendimento;
            }

            atendimento.IdConversa = idConversa;
            atendimento.IdCliente = scope.IdCliente;
            atendimento.IdEstabelecimento = scope.IdEstabelecimento;
            atendimento.TelefoneE164 = string.IsNullOrWhiteSpace(telefone) ? atendimento.TelefoneE164 : telefone;
            atendimento.NomeCliente = string.IsNullOrWhiteSpace(cliente?.Nome) ? atendimento.NomeCliente : cliente!.Nome!.Trim();
            atendimento.IntencaoPrincipal = intencaoPrincipal;
            atendimento.IntencaoDetalhe = intencaoDetalhe;
            atendimento.IdServico = servico?.Id ?? atendimento.IdServico;
            atendimento.NomeServico = servico?.Nome ?? atendimento.NomeServico;
            atendimento.CategoriaServico = servico?.Tipo ?? atendimento.CategoriaServico;
            atendimento.ResumoAtendimento = BuildResumoAtendimento(cliente?.Nome, servico, intencaoPrincipal, status);
            atendimento.Status = status;
            atendimento.EtapaAtual = etapaAtual;
            atendimento.UltimaPergunta = ultimaPergunta;
            atendimento.ViaNumeroCentral = viaNumeroCentral;

            if (string.Equals(status, "aguardando_interno", StringComparison.OrdinalIgnoreCase))
            {
                atendimento.DataHandover ??= DateTime.UtcNow;
            }

            if (string.Equals(status, "concluido", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "cancelado", StringComparison.OrdinalIgnoreCase))
            {
                atendimento.DataConclusao ??= DateTime.UtcNow;
            }

            foreach (var extra in extras)
            {
                atendimento.DadosExtras[extra.Key] = extra.Value;
            }

            await _servicoAtendimentoRepository.AtualizarAsync(atendimento);
            return atendimento;
        }

        private async Task<string?> GarantirTelefoneClienteAsync(Guid idConversa, EffectiveConversationScope scope)
        {
            if (!string.IsNullOrWhiteSpace(scope.TelefoneCliente))
            {
                return scope.TelefoneCliente;
            }

            var telefoneCliente = await _clienteRepository.ObterTelefoneClienteAsync(scope.IdCliente, scope.IdEstabelecimento);
            if (!string.IsNullOrWhiteSpace(telefoneCliente))
            {
                return telefoneCliente;
            }

            var conversa = await _conversationRepository.ObterPorIdAsync(idConversa);
            return conversa?.TelefoneCliente;
        }

        private async Task SalvarContextoServicoAsync(
            Guid idConversa,
            ConversationContext? contextoAtual,
            string estado,
            Guid? atendimentoId,
            IReadOnlyDictionary<string, object?> extras)
        {
            var dados = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (atendimentoId.HasValue && atendimentoId.Value != Guid.Empty)
            {
                dados[ChaveAtendimentoId] = atendimentoId.Value.ToString();
            }

            foreach (var extra in extras)
            {
                if (extra.Value != null)
                {
                    dados[extra.Key] = extra.Value;
                }
            }

            var novoContexto = new ConversationContext
            {
                Estado = estado,
                DadosColetados = dados,
                ExpiracaoEstado = null
            };

            var contextoMesclado = CentralRoutingService.MergeCentralSelection(contextoAtual, novoContexto);
            await _conversationRepository.SalvarContextoAsync(idConversa, contextoMesclado);
        }

        private async Task LimparContextoPreservandoSelecaoAsync(Guid idConversa)
        {
            var contextoAtual = await _conversationRepository.ObterContextoAsync(idConversa);
            var preservado = CentralRoutingService.BuildPreservedSelectionContext(contextoAtual);
            if (preservado != null)
            {
                await _conversationRepository.SalvarContextoAsync(idConversa, preservado);
                return;
            }

            await _conversationRepository.LimparContextoAsync(idConversa);
        }

        private static bool DeveInterceptarNovaMensagem(string mensagemTexto, IReadOnlyList<ServicoCatalogItem> catalogo)
        {
            if (string.IsNullOrWhiteSpace(mensagemTexto))
            {
                return false;
            }

            if (EhPerguntaAmplaDeServicos(mensagemTexto) ||
                UsuarioPerguntouPreco(mensagemTexto) ||
                UsuarioPerguntouDuracao(mensagemTexto))
            {
                return true;
            }

            return MatchServices(mensagemTexto, catalogo).Count > 0;
        }

        private static bool TemContextoDeOutroFluxo(string? estado)
        {
            if (string.IsNullOrWhiteSpace(estado))
            {
                return false;
            }

            if (IsServiceState(estado) ||
                string.Equals(estado, CentralRoutingService.EstadoAguardandoEscolha, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(estado, CentralRoutingService.EstadoEstabelecimentoSelecionado, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return estado.StartsWith("oficina_", StringComparison.OrdinalIgnoreCase) ||
                   estado.StartsWith("garagem_", StringComparison.OrdinalIgnoreCase) ||
                   estado.StartsWith("nautica_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsServiceState(string? estado)
        {
            return string.Equals(estado, EstadoAguardandoCategoria, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(estado, EstadoAguardandoEscolha, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(estado, EstadoAguardandoVeiculo, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(estado, EstadoAguardandoConfirmacaoHumano, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOpenStatus(string? status)
        {
            return string.Equals(status, "em_triagem", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "aguardando_cliente", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "aguardando_interno", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "em_andamento", StringComparison.OrdinalIgnoreCase);
        }

        private static bool PodeContinuarServicoAtual(string mensagemTexto, ServicoAtendimento atendimentoAtual)
        {
            if (string.IsNullOrWhiteSpace(mensagemTexto))
            {
                return false;
            }

            if (UsuarioPerguntouPreco(mensagemTexto) ||
                UsuarioPerguntouDuracao(mensagemTexto) ||
                EhMensagemEncerramento(mensagemTexto) ||
                FoiPedidoHumano(mensagemTexto))
            {
                return true;
            }

            if (ObterBool(atendimentoAtual.DadosExtras, ChaveUsuarioPerguntouPreco) ||
                ObterBool(atendimentoAtual.DadosExtras, ChaveUsuarioPerguntouDuracao))
            {
                return mensagemTexto.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 8;
            }

            return ContemAlgum(mensagemTexto, "como funciona", "me explica", "detalhes", "serve para", "esse servico", "esse serviço");
        }

        private static string BuildResumoAtendimento(
            string? nomeCliente,
            ServicoCatalogItem? servico,
            string intencaoPrincipal,
            string status)
        {
            var nome = string.IsNullOrWhiteSpace(nomeCliente) ? "Cliente" : nomeCliente.Trim();
            if (servico == null)
            {
                return $"{nome} entrou em fluxo de servicos. Intencao={intencaoPrincipal}. Status={status}.";
            }

            return $"{nome} falou sobre {servico.Nome}. Intencao={intencaoPrincipal}. Status={status}.";
        }

        private static string BuildFactsPrompt(string summary, IReadOnlyCollection<string> detalhes, string? proximaPergunta)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Redija uma resposta curta, objetiva e natural.");
            builder.AppendLine($"Resumo autorizado: {summary}");

            foreach (var detalhe in detalhes.Where(item => !string.IsNullOrWhiteSpace(item)))
            {
                builder.AppendLine($"Detalhe autorizado: {detalhe}");
            }

            if (!string.IsNullOrWhiteSpace(proximaPergunta))
            {
                builder.AppendLine($"Feche com esta proxima pergunta: {proximaPergunta}");
            }

            builder.AppendLine("Nao invente fatos e nao liste catalogo completo.");
            return builder.ToString().TrimEnd();
        }

        private static string BuildServiceSummary(ServicoCatalogItem servico)
        {
            var descricao = string.IsNullOrWhiteSpace(servico.Descricao)
                ? $"Fazemos {servico.Nome}."
                : $"Fazemos {servico.Nome}. {TrimSentence(servico.Descricao!, 140)}";

            return descricao.Trim();
        }

        private static string? BuildPriceText(ServicoCatalogItem servico, ServicoCatalogVehicleItem? vehicle)
        {
            if (!servico.DiferePorVeiculo)
            {
                var valor = servico.ValorCentavos ?? servico.ValorMinimoCentavos ?? servico.ValorMaximoCentavos;
                return valor.HasValue
                    ? $"Hoje esse servico esta em {FormatCurrency(valor.Value)}."
                    : null;
            }

            if (vehicle == null)
            {
                return null;
            }

            var faixa = ObterFaixaPrecoVeiculo(vehicle);
            if (!faixa.Min.HasValue || !faixa.Max.HasValue)
            {
                return null;
            }

            if (faixa.Min.Value == faixa.Max.Value && !TemVariacaoPorMarca(vehicle))
            {
                return $"Para {vehicle.NomeExibicao}, esse servico fica em {FormatCurrency(faixa.Min.Value)}.";
            }

            return $"Para {vehicle.NomeExibicao}, hoje eu tenho uma faixa de {FormatCurrency(faixa.Min.Value)} a {FormatCurrency(faixa.Max.Value)}. Esse valor varia conforme a marca da peca.";
        }

        private static (long? Min, long? Max) ObterFaixaPrecoVeiculo(ServicoCatalogVehicleItem vehicle)
        {
            var candidatos = new List<long>();
            if (vehicle.ValorCentavos.HasValue)
            {
                candidatos.Add(vehicle.ValorCentavos.Value);
            }

            if (vehicle.ValorMinimoCentavos.HasValue)
            {
                candidatos.Add(vehicle.ValorMinimoCentavos.Value);
            }

            if (vehicle.ValorMaximoCentavos.HasValue)
            {
                candidatos.Add(vehicle.ValorMaximoCentavos.Value);
            }

            candidatos.AddRange(vehicle.MarcasPeca
                .SelectMany(item => new[] { item.ValorCentavos, item.ValorMinimoCentavos, item.ValorMaximoCentavos })
                .Where(item => item.HasValue)
                .Select(item => item!.Value));

            if (candidatos.Count == 0)
            {
                return (null, null);
            }

            return (candidatos.Min(), candidatos.Max());
        }

        private static bool TemVariacaoPorMarca(ServicoCatalogVehicleItem vehicle)
        {
            var valores = vehicle.MarcasPeca
                .SelectMany(item => new[] { item.ValorCentavos, item.ValorMinimoCentavos, item.ValorMaximoCentavos })
                .Where(item => item.HasValue)
                .Select(item => item!.Value)
                .Distinct()
                .ToArray();

            return valores.Length > 1;
        }

        private static string FormatCurrency(long valorCentavos)
        {
            var valor = valorCentavos / 100m;
            return valor.ToString("C", CultureInfo.GetCultureInfo("pt-BR"));
        }

        private static string FormatDuration(int duracaoMinutos)
        {
            if (duracaoMinutos <= 0)
            {
                return "tempo nao configurado";
            }

            var horas = duracaoMinutos / 60;
            var minutos = duracaoMinutos % 60;
            if (horas <= 0)
            {
                return $"{minutos} min";
            }

            if (minutos == 0)
            {
                return horas == 1 ? "1h" : $"{horas}h";
            }

            return $"{horas}h {minutos}min";
        }

        private static string TrimSentence(string texto, int maxLength)
        {
            var limpo = EspacosRegex.Replace(texto.Trim(), " ");
            if (limpo.Length <= maxLength)
            {
                return limpo;
            }

            return $"{limpo[..Math.Max(0, maxLength - 3)].Trim()}...";
        }

        private static IReadOnlyList<ServicoMatch> MatchServices(string mensagemTexto, IReadOnlyList<ServicoCatalogItem> catalogo)
        {
            var normalizado = NormalizeText(mensagemTexto);
            var tokensMensagem = Tokenize(normalizado);
            var matches = new List<ServicoMatch>();

            foreach (var servico in catalogo)
            {
                var score = 0;
                var nomeNormalizado = NormalizeText(servico.Nome);
                var tipoNormalizado = NormalizeText(servico.Tipo);
                var descricaoNormalizada = NormalizeText(servico.Descricao);

                if (!string.IsNullOrWhiteSpace(nomeNormalizado) && normalizado.Contains(nomeNormalizado, StringComparison.Ordinal))
                {
                    score += 120;
                }

                foreach (var keyword in servico.PalavrasChave.Where(item => !string.IsNullOrWhiteSpace(item)))
                {
                    var keywordNormalizada = NormalizeText(keyword);
                    if (!string.IsNullOrWhiteSpace(keywordNormalizada) && normalizado.Contains(keywordNormalizada, StringComparison.Ordinal))
                    {
                        score += 45;
                    }
                }

                if (!string.IsNullOrWhiteSpace(tipoNormalizado) && normalizado.Contains(tipoNormalizado, StringComparison.Ordinal))
                {
                    score += 25;
                }

                var tokensNome = Tokenize(nomeNormalizado);
                var overlapNome = tokensNome.Count(token => tokensMensagem.Contains(token));
                score += Math.Min(45, overlapNome * 15);

                var tokensDescricao = Tokenize(descricaoNormalizada);
                var overlapDescricao = tokensDescricao.Count(token => tokensMensagem.Contains(token));
                score += Math.Min(15, overlapDescricao * 5);

                if (score > 0)
                {
                    matches.Add(new ServicoMatch(servico, score));
                }
            }

            return matches
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Servico.Nome)
                .ToArray();
        }

        private static IReadOnlyList<ServicoMatch> SelecionarMelhoresCandidatos(IReadOnlyList<ServicoMatch> matches)
        {
            if (matches.Count == 0)
            {
                return Array.Empty<ServicoMatch>();
            }

            var melhor = matches[0];
            if (matches.Count == 1)
            {
                return new[] { melhor };
            }

            if (melhor.Score >= 100 && matches[1].Score <= melhor.Score - 25)
            {
                return new[] { melhor };
            }

            var proximos = matches
                .Where(item => item.Score >= Math.Max(35, melhor.Score - 15))
                .Take(3)
                .ToArray();

            if (proximos.Length == 0)
            {
                return new[] { melhor };
            }

            return proximos;
        }

        private static ServicoCatalogItem? ResolverServicoSelecionado(string mensagemTexto, IReadOnlyCollection<ServicoCatalogItem> candidatos)
        {
            if (candidatos.Count == 0)
            {
                return null;
            }

            var nomes = candidatos.Select(item => item.Nome).ToArray();
            var nomeEscolhido = ResolverOpcaoPorTextoOuIndice(mensagemTexto, nomes, "servicos_sel_");
            if (!string.IsNullOrWhiteSpace(nomeEscolhido))
            {
                return candidatos.FirstOrDefault(item => string.Equals(item.Nome, nomeEscolhido, StringComparison.OrdinalIgnoreCase));
            }

            var normalizado = NormalizeText(mensagemTexto);
            return candidatos.FirstOrDefault(item =>
                string.Equals(NormalizeText(item.Nome), normalizado, StringComparison.Ordinal) ||
                normalizado.Contains(NormalizeText(item.Nome), StringComparison.Ordinal));
        }

        private static ServicoCatalogVehicleItem? ResolverVeiculoSelecionado(
            string mensagemTexto,
            ServicoCatalogItem servico,
            ConversationContext contextoAtual)
        {
            var options = ObterListaTexto(contextoAtual.DadosColetados, ChaveVehicleOptions);
            var escolhido = ResolverOpcaoPorTextoOuIndice(mensagemTexto, options, "servicos_veh_");
            if (!string.IsNullOrWhiteSpace(escolhido))
            {
                return servico.Veiculos.FirstOrDefault(item => string.Equals(item.NomeExibicao, escolhido, StringComparison.OrdinalIgnoreCase));
            }

            var normalizado = NormalizeText(mensagemTexto);
            var match = servico.Veiculos
                .Where(item => !string.IsNullOrWhiteSpace(item.NomeExibicao))
                .Select(item => new { Vehicle = item, Nome = NormalizeText(item.NomeExibicao) })
                .FirstOrDefault(item => normalizado.Contains(item.Nome, StringComparison.Ordinal) || item.Nome.Contains(normalizado, StringComparison.Ordinal));

            return match?.Vehicle;
        }

        private static ServicoCatalogItem? ResolverServicoDoContextoOuAtendimento(
            ConversationContext? contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            IReadOnlyList<ServicoCatalogItem> catalogo)
        {
            var id = ObterGuid(contextoAtual?.DadosColetados, ChaveServicoId)
                ?? atendimentoAtual?.IdServico;

            return id.HasValue
                ? catalogo.FirstOrDefault(item => item.Id == id.Value)
                : null;
        }

        private static ServicoCatalogVehicleItem? ObterVeiculoAtual(ServicoAtendimento? atendimentoAtual, ServicoCatalogItem servico)
        {
            var vehicleId = ObterGuid(atendimentoAtual?.DadosExtras, ChaveVehicleId);
            if (vehicleId.HasValue)
            {
                return servico.Veiculos.FirstOrDefault(item => item.CarroId == vehicleId.Value);
            }

            var vehicleNome = ObterTexto(atendimentoAtual?.DadosExtras, ChaveVehicleNome);
            return string.IsNullOrWhiteSpace(vehicleNome)
                ? null
                : servico.Veiculos.FirstOrDefault(item => string.Equals(item.NomeExibicao, vehicleNome, StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryObterServicoPorId(IReadOnlyList<ServicoCatalogItem> catalogo, Guid idServico, out ServicoCatalogItem servico)
        {
            servico = catalogo.FirstOrDefault(item => item.Id == idServico) ?? new ServicoCatalogItem();
            return servico.Id != Guid.Empty;
        }

        private static IReadOnlyList<WhatsAppReplyButtonOption> BuildSimNaoButtons()
        {
            return new[]
            {
                new WhatsAppReplyButtonOption("servicos_humano_sim", "Sim"),
                new WhatsAppReplyButtonOption("servicos_humano_nao", "Nao")
            };
        }

        private static IReadOnlyList<WhatsAppReplyButtonOption> BuildIndexedButtons(string prefix, IReadOnlyList<string> options)
        {
            return options
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Take(3)
                .Select((item, index) => new WhatsAppReplyButtonOption($"{prefix}{index + 1}", TrimButtonTitle(item)))
                .ToArray();
        }

        private static string TrimButtonTitle(string value)
        {
            var limpo = value.Trim();
            return limpo.Length <= 20 ? limpo : $"{limpo[..17].Trim()}...";
        }

        private static string? ResolverOpcaoPorTextoOuIndice(string mensagemTexto, IReadOnlyList<string> options, string buttonPrefix)
        {
            if (options.Count == 0)
            {
                return null;
            }

            var texto = NormalizeText(mensagemTexto);
            var prefixoNormalizado = NormalizeText(buttonPrefix);
            if (texto.StartsWith(prefixoNormalizado, StringComparison.Ordinal))
            {
                var sufixo = texto[prefixoNormalizado.Length..];
                if (int.TryParse(sufixo, out var indice) && indice >= 1 && indice <= options.Count)
                {
                    return options[indice - 1];
                }
            }

            if (int.TryParse(texto, out var numero) && numero >= 1 && numero <= options.Count)
            {
                return options[numero - 1];
            }

            var matchOpcao = Regex.Match(texto, @"(?:opcao|opcao numero|numero)\s*(\d{1,2})", RegexOptions.CultureInvariant);
            if (matchOpcao.Success &&
                int.TryParse(matchOpcao.Groups[1].Value, out numero) &&
                numero >= 1 &&
                numero <= options.Count)
            {
                return options[numero - 1];
            }

            return options.FirstOrDefault(item =>
                string.Equals(NormalizeText(item), texto, StringComparison.Ordinal) ||
                texto.Contains(NormalizeText(item), StringComparison.Ordinal));
        }

        private static string NormalizeText(string? texto)
        {
            if (string.IsNullOrWhiteSpace(texto))
            {
                return string.Empty;
            }

            var normalized = texto.Normalize(NormalizationForm.FormD);
            var chars = normalized.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark);
            return EspacosRegex.Replace(new string(chars.ToArray()).ToLowerInvariant(), " ").Trim();
        }

        private static HashSet<string> Tokenize(string? texto)
        {
            return TokenRegex
                .Matches(NormalizeText(texto))
                .Select(match => match.Value)
                .Where(token => token.Length > 2 && !StopWords.Contains(token))
                .ToHashSet(StringComparer.Ordinal);
        }

        private static bool EhPerguntaAmplaDeServicos(string texto)
        {
            return ContemAlgum(
                texto,
                "quais servicos",
                "quais serviços",
                "o que voces fazem",
                "o que vocês fazem",
                "que servicos voces fazem",
                "trabalham com",
                "servicos disponiveis",
                "serviços disponiveis",
                "catalogo de servicos",
                "catalogo de serviços");
        }

        private static bool UsuarioPerguntouPreco(string texto)
        {
            return ContemAlgum(
                texto,
                "preco",
                "preço",
                "valor",
                "quanto custa",
                "quanto fica",
                "orcamento",
                "orçamento",
                "cotacao",
                "cotação");
        }

        private static bool UsuarioPerguntouDuracao(string texto)
        {
            return ContemAlgum(
                texto,
                "quanto tempo",
                "demora",
                "duracao",
                "duração",
                "tempo estimado");
        }

        private static bool FoiPedidoHumano(string texto)
        {
            return ContemAlgum(
                texto,
                "falar com atendente",
                "falar com humano",
                "falar com uma pessoa",
                "falar com alguem",
                "quero um atendente",
                "quero falar com a equipe",
                "atendimento humano",
                "me passa para a equipe");
        }

        private static bool EhConfirmacaoPositiva(string texto)
        {
            var normalizado = NormalizeText(texto);
            return normalizado is "sim" or "s" or "ok" or "quero" or "pode ser" or "servicos_humano_sim";
        }

        private static bool EhConfirmacaoNegativa(string texto)
        {
            var normalizado = NormalizeText(texto);
            return normalizado is "nao" or "não" or "n" or "agora nao" or "agora não" or "deixa" or "servicos_humano_nao";
        }

        private static bool EhMensagemEncerramento(string texto)
        {
            return ContemAlgum(texto, "obrigado", "obrigada", "valeu", "era isso", "so isso", "só isso");
        }

        private static bool ContemAlgum(string? texto, params string[] termos)
        {
            var normalizado = NormalizeText(texto);
            return termos.Any(termo => normalizado.Contains(NormalizeText(termo), StringComparison.Ordinal));
        }

        private static Dictionary<string, object?> CriarExtras(IReadOnlyDictionary<string, object?> extras)
        {
            return new Dictionary<string, object?>(extras, StringComparer.OrdinalIgnoreCase);
        }

        private static string? ObterTexto(IReadOnlyDictionary<string, object?>? dados, string chave)
        {
            if (dados == null || !dados.TryGetValue(chave, out var valor) || valor == null)
            {
                return null;
            }

            return valor switch
            {
                string texto => texto,
                Guid guid => guid.ToString(),
                JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
                JsonElement json => json.ToString(),
                _ => valor.ToString()
            };
        }

        private static Guid? ObterGuid(IReadOnlyDictionary<string, object?>? dados, string chave)
        {
            var valor = ObterTexto(dados, chave);
            return Guid.TryParse(valor, out var guid) ? guid : null;
        }

        private static bool ObterBool(IReadOnlyDictionary<string, object?>? dados, string chave)
        {
            if (dados == null || !dados.TryGetValue(chave, out var valor) || valor == null)
            {
                return false;
            }

            return valor switch
            {
                bool boolValue => boolValue,
                JsonElement json when json.ValueKind is JsonValueKind.True or JsonValueKind.False => json.GetBoolean(),
                JsonElement json when json.ValueKind == JsonValueKind.String && bool.TryParse(json.GetString(), out var parsed) => parsed,
                string texto when bool.TryParse(texto, out var parsed) => parsed,
                _ => false
            };
        }

        private static int ObterInt(IReadOnlyDictionary<string, object?>? dados, string chave)
        {
            if (dados == null || !dados.TryGetValue(chave, out var valor) || valor == null)
            {
                return 0;
            }

            return valor switch
            {
                int intValue => intValue,
                long longValue => (int)longValue,
                JsonElement json when json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var parsedNumber) => parsedNumber,
                JsonElement json when json.ValueKind == JsonValueKind.String && int.TryParse(json.GetString(), out var parsedText) => parsedText,
                string texto when int.TryParse(texto, out var parsedString) => parsedString,
                _ => 0
            };
        }

        private static string[] ObterListaTexto(IReadOnlyDictionary<string, object?>? dados, string chave)
        {
            if (dados == null || !dados.TryGetValue(chave, out var valor) || valor == null)
            {
                return Array.Empty<string>();
            }

            if (valor is string[] array)
            {
                return array;
            }

            if (valor is IReadOnlyCollection<string> ro)
            {
                return ro.ToArray();
            }

            if (valor is IEnumerable<string> enumerable)
            {
                return enumerable.ToArray();
            }

            if (valor is JsonElement json && json.ValueKind == JsonValueKind.Array)
            {
                return json.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Cast<string>()
                    .ToArray();
            }

            return Array.Empty<string>();
        }

        private sealed record ServicoMatch(ServicoCatalogItem Servico, int Score);
    }
}
