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
using APIBack.Model;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public partial class ServicosFlowService
    {
        private const string EstadoAguardandoCategoria = "servicos_aguardando_categoria";
        private const string EstadoAguardandoEscolha = "servicos_aguardando_escolha";
        public const string EstadoAguardandoNome = "servicos_aguardando_nome";
        public const string EstadoAguardandoServico = "servicos_aguardando_servico";
        public const string EstadoAguardandoVeiculo = "servicos_aguardando_veiculo";
        public const string EstadoOfertaDetalhes = "servicos_oferta_detalhes";
        public const string EstadoAguardandoMarca = "servicos_aguardando_marca";
        public const string EstadoAguardandoConfirmacaoFinal = "servicos_aguardando_confirmacao_final";
        public const string EstadoProntoAgendamento = "servicos_pronto_agendamento";
        public const string EstadoAguardandoConfirmacaoHumano = "servicos_aguardando_confirmacao_humano";

        private const string ChaveAtendimentoId = "servicos_atendimento_id";
        private const string ChavePerguntaPendente = "servicos_pergunta_pendente";
        private const string ChaveMensagemPendente = "servicos_mensagem_pendente";
        private const string ChaveCandidatosIds = "servicos_candidatos_ids";
        private const string ChaveCategoriaOpcoes = "servicos_categoria_opcoes";
        private const string ChaveServicoId = "servicos_servico_travado";
        private const string ChaveServicoNome = "servicos_servico_nome";
        private const string ChaveItensDisponiveis = "servicos_itens_disponiveis";
        private const string ChaveItensEntregues = "servicos_itens_entregues";
        private const string ChaveTentativaSemMatch = "servicos_tentativa_sem_match";
        private const string ChaveTrocaPendenteCampo = "servicos_troca_pendente_campo";
        private const string ChaveTrocaPendenteValor = "servicos_troca_pendente_valor";
        private const string ChaveTrocaPendenteLabel = "servicos_troca_pendente_label";
        private const string ChaveUltimaSolicitacaoHumano = "servicos_ultima_solicitacao_humano";
        private const string ChaveTentativas = "servicos_tentativas";
        private const string ChaveUsuarioPerguntouPreco = "servicos_usuario_perguntou_preco";
        private const string ChaveUsuarioPerguntouDuracao = "servicos_usuario_perguntou_duracao";
        private const string ChaveVehicleId = "servicos_veiculo_travado";
        private const string ChaveVehicleNome = "servicos_veiculo_nome";
        private const string ChaveVehicleOptions = "servicos_vehicle_options";
        private const string ChaveVehiclePromptReason = "servicos_vehicle_prompt_reason";
        private const string ChaveMarcaPecaId = "servicos_marca_travada";
        private const string ChaveMarcaPecaNome = "servicos_marca_nome";
        private const string PerguntaNome = "informar_nome";
        private const string PerguntaServico = "informar_servico";
        private const string PerguntaVeiculo = "informar_veiculo";
        private const string PerguntaDetalhes = "oferta_detalhes";
        private const string PerguntaMarca = "selecionar_marca";
        private const string PerguntaConfirmacaoFinal = "confirmacao_final";
        private const string PerguntaConfirmacaoTroca = "confirmacao_troca";
        private const string PerguntaConfirmacaoHumano = "confirmacao_humano";

        private static readonly Regex EspacosRegex = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex TokenRegex = new(@"[a-z0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex PrefixoNomeRegex = new(
            @"^(meu nome e|me chamo|pode me chamar de|sou o|sou a|sou)\s+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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

            var catalogo = await _catalogProvider.ObterCatalogoVisivelAsync(scope.IdEstabelecimento);
            if (catalogo.Count == 0)
            {
                return (false, null);
            }

            var atendimentoAtual = await ObterAtendimentoAtualAsync(idConversa, scope);
            var texto = mensagemTexto?.Trim() ?? string.Empty;
            var viaNumeroCentral = _centralRouting.IsCentralDisplayPhone(phoneNumberDisplay);
            var estadoDeterministico = ResolveDeterministicState(contextoAtual?.Estado, atendimentoAtual?.EtapaAtual);

            if (!string.IsNullOrWhiteSpace(estadoDeterministico))
            {
                if (atendimentoAtual != null &&
                    IsOpenStatus(atendimentoAtual.Status) &&
                    !string.Equals(atendimentoAtual.Status, "aguardando_interno", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(atendimentoAtual.Status, "em_andamento", StringComparison.OrdinalIgnoreCase))
                {
                    atendimentoAtual.Status = "em_triagem";
                    atendimentoAtual.EtapaAtual = estadoDeterministico;
                    await _servicoAtendimentoRepository.AtualizarAsync(atendimentoAtual);
                }

                var contextoFluxo = contextoAtual ?? new ConversationContext
                {
                    Estado = estadoDeterministico,
                    DadosColetados = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                };

                var decisaoEstado = await ProcessarFluxoDeterministicoAsync(
                    idConversa,
                    scope,
                    cliente,
                    catalogo,
                    contextoFluxo,
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

            if (!DeveInterceptarFluxoDeterministico(texto, catalogo, atendimentoAtual))
            {
                return (false, null);
            }

            var decisao = await IniciarFluxoDeterministicoAsync(
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

            var vehicle = await ResolverVeiculoSelecionadoAsync(
                scope.IdEstabelecimento,
                mensagemTexto,
                servico,
                contextoAtual);
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

            return new AssistantDecision(reply, "none", null, false, null);
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
            var pricePending = priceAsked || UsuarioPerguntouPreco(mensagemTexto) || ObterBool(atendimentoAtual?.DadosExtras, ChaveUsuarioPerguntouPreco);
            var durationPending = durationAsked || UsuarioPerguntouDuracao(mensagemTexto) || ObterBool(atendimentoAtual?.DadosExtras, ChaveUsuarioPerguntouDuracao);
            var brandAsked = UsuarioPerguntouMarcasPeca(mensagemTexto);
            var detailsAsked = UsuarioPerguntouDetalhesServico(mensagemTexto);

            vehicle ??= ObterVeiculoAtual(atendimentoAtual, servico);

            if (servico.DiferePorVeiculo)
            {
                vehicle ??= await ResolverVeiculoSelecionadoAsync(
                    scope.IdEstabelecimento,
                    mensagemTexto,
                    servico,
                    contextoAtual);
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
                    pricePending,
                    durationPending,
                    viaNumeroCentral,
                    prefixo: BuildResumoDoQueJaSei(cliente, atendimentoAtual, servico, null, null));
            }

            if (vehicle != null && !vehicle.Compativel)
            {
                var incompatibilidade = $"Ja entendi que voce quer {servico.Nome}, mas ainda nao encontrei compatibilidade confirmada para {vehicle.NomeExibicao}. Se quiser, eu passo isso para a equipe conferir com voce.";
                return await PerguntarConfirmacaoHumanoAsync(
                    idConversa,
                    scope,
                    cliente,
                    atendimentoAtual,
                    contextoAtual,
                    servico,
                    incompatibilidade,
                    mensagemTexto,
                    viaNumeroCentral);
            }

            var marcaSelecionada = vehicle == null
                ? null
                : ResolverMarcaPecaSelecionada(mensagemTexto, vehicle, atendimentoAtual);

            var extras = new Dictionary<string, object?>
            {
                [ChaveUsuarioPerguntouPreco] = pricePending,
                [ChaveUsuarioPerguntouDuracao] = durationPending
            };

            if (vehicle != null)
            {
                extras[ChaveVehicleId] = vehicle.CarroId.ToString();
                extras[ChaveVehicleNome] = vehicle.NomeExibicao;
            }

            if (marcaSelecionada != null)
            {
                extras[ChaveMarcaPecaId] = marcaSelecionada.Id;
                extras[ChaveMarcaPecaNome] = marcaSelecionada.Nome;
            }

            string reply;
            string? proximaPergunta = null;
            var intencaoPrincipal = "duvida_servicos";
            var etapaAtual = "servico_identificado";

            if (brandAsked)
            {
                reply = BuildBrandsReply(servico, vehicle, marcaSelecionada);
                proximaPergunta = marcaSelecionada == null
                    ? "Se quiser, depois eu te passo o valor da marca que fizer mais sentido."
                    : "Se quiser, eu tambem posso te passar o valor dessa opcao.";
                etapaAtual = marcaSelecionada == null ? "aguardando_cliente" : "marca_identificada";
            }
            else if (pricePending || durationPending || detailsAsked || marcaSelecionada != null)
            {
                var partes = new List<string>();

                if (detailsAsked)
                {
                    partes.Add(BuildServiceDetailsText(servico, vehicle));
                }

                if (marcaSelecionada != null && pricePending)
                {
                    var textoMarca = BuildPiecePriceText(servico, vehicle, marcaSelecionada);
                    if (textoMarca != null)
                    {
                        partes.Add(textoMarca);
                    }
                }
                else if (pricePending)
                {
                    var textoPreco = BuildPriceText(servico, vehicle);
                    if (textoPreco == null)
                    {
                        var semPreco = $"Eu ainda nao tenho o valor configurado para {servico.Nome} no catalogo. Se quiser, eu passo isso para a equipe.";
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

                    partes.Add(textoPreco);
                    intencaoPrincipal = "preco_servico";
                }
                else if (marcaSelecionada != null)
                {
                    partes.Add($"Perfeito. Para {vehicle!.NomeExibicao}, eu anotei a marca {marcaSelecionada.Nome}.");
                }

                if (durationPending)
                {
                    partes.Add(servico.DuracaoMinutos > 0
                        ? $"O tempo medio desse servico e {FormatDuration(servico.DuracaoMinutos)}."
                        : "Ainda nao tenho o tempo configurado no catalogo.");
                    intencaoPrincipal = durationPending && !pricePending ? "duracao_servico" : intencaoPrincipal;
                }

                if (partes.Count == 0)
                {
                    partes.Add(BuildServiceReadyReply(servico, vehicle));
                }

                proximaPergunta = BuildFollowUpPrompt(servico, vehicle, pricePending, durationPending);
                if (!string.IsNullOrWhiteSpace(proximaPergunta))
                {
                    partes.Add(proximaPergunta);
                }

                reply = string.Join(" ", partes.Where(item => !string.IsNullOrWhiteSpace(item))).Trim();
                etapaAtual = marcaSelecionada != null
                    ? "marca_identificada"
                    : vehicle != null
                        ? "veiculo_identificado"
                        : "servico_identificado";
            }
            else
            {
                reply = BuildServiceReadyReply(servico, vehicle);
                proximaPergunta = BuildFollowUpPrompt(servico, vehicle, false, false);
                if (!string.IsNullOrWhiteSpace(proximaPergunta))
                {
                    reply = $"{reply} {proximaPergunta}".Trim();
                }

                etapaAtual = vehicle != null ? "veiculo_identificado" : "servico_identificado";
            }

            await ObterOuCriarAtendimentoAsync(
                idConversa,
                scope,
                cliente,
                atendimentoAtual,
                intencaoPrincipal,
                servico.Tipo,
                servico,
                status: "aguardando_cliente",
                etapaAtual,
                ultimaPergunta: proximaPergunta,
                viaNumeroCentral,
                CriarExtras(extras));

            await LimparContextoPreservandoSelecaoAsync(idConversa);
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

            var pergunta = categorias.Length == 1
                ? $"Hoje eu consigo te ajudar com {categorias[0]}. Se for isso, me responde com o nome do servico ou me diz que quer seguir por essa categoria."
                : $"Para eu te orientar melhor, me diz qual dessas categorias faz mais sentido agora:\n{FormatarOpcoesEnumeradas(categorias)}";

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

            return new AssistantDecision(pergunta, "none", null, false, null);
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
            var pergunta = nomes.Length == 1
                ? $"Voce esta falando de {nomes[0]}?"
                : $"Achei estes servicos parecidos. Me responde com o numero ou com o nome:\n{FormatarOpcoesEnumeradas(nomes)}";

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

            return new AssistantDecision(pergunta, "none", null, false, null);
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
            bool viaNumeroCentral,
            string? prefixo = null)
        {
            var options = servico.Veiculos
                .Where(item => !string.IsNullOrWhiteSpace(item.NomeExibicao))
                .Select(item => item.NomeExibicao)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToArray();

            var perguntaBase = options.Length == 0
                ? $"Agora me fala a marca e o modelo do veiculo para eu conferir {servico.Nome}."
                : $"Agora me fala a marca e o modelo do veiculo para eu conferir {servico.Nome}. Exemplo: {string.Join(" ou ", options)}.";

            var pergunta = string.IsNullOrWhiteSpace(prefixo)
                ? perguntaBase
                : $"{prefixo} {perguntaBase}".Trim();

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

            return new AssistantDecision(pergunta, "none", null, false, null);
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
            var reply = $"{mensagemBase} Se quiser, eu passo isso para a equipe. Me responde com sim ou nao.";
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

            return new AssistantDecision(reply, "none", null, false, null);
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
            var servicoAtual = ResolverServicoDoContextoOuAtendimento(contextoAtual, atendimentoAtual, catalogo);
            if (servicoAtual != null && servicoAtual.DiferePorVeiculo)
            {
                return await PerguntarVeiculoAsync(
                    idConversa,
                    scope,
                    cliente,
                    atendimentoAtual,
                    contextoAtual,
                    servicoAtual,
                    ObterBool(contextoAtual?.DadosColetados, ChaveUsuarioPerguntouPreco) || ObterBool(atendimentoAtual?.DadosExtras, ChaveUsuarioPerguntouPreco),
                    ObterBool(contextoAtual?.DadosColetados, ChaveUsuarioPerguntouDuracao) || ObterBool(atendimentoAtual?.DadosExtras, ChaveUsuarioPerguntouDuracao),
                    viaNumeroCentral,
                    prefixo: BuildResumoDoQueJaSei(cliente, atendimentoAtual, servicoAtual, null, null, mencionarPendenciaVeiculo: true));
            }

            var tentativaAtual = ObterInt(contextoAtual?.DadosColetados, ChaveTentativaSemMatch);
            if (tentativaAtual <= 0)
            {
                var veiculoAtual = servicoAtual == null ? null : ObterVeiculoAtual(atendimentoAtual, servicoAtual);
                var resumo = BuildResumoDoQueJaSei(cliente, atendimentoAtual, servicoAtual, veiculoAtual, null);
                var basePergunta = "Ainda nao consegui identificar o servico. Me fala o nome do servico ou a categoria principal.";
                var pergunta = string.Join(" ",
                    new[] { prefixo, resumo, basePergunta }
                        .Where(item => !string.IsNullOrWhiteSpace(item)));

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

                return new AssistantDecision(pergunta, "none", null, false, null);
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

            if (contextoAtual?.DadosColetados != null)
            {
                foreach (var item in contextoAtual.DadosColetados)
                {
                    if (item.Value != null)
                    {
                        dados[item.Key] = item.Value;
                    }
                }
            }

            if (atendimentoId.HasValue && atendimentoId.Value != Guid.Empty)
            {
                dados[ChaveAtendimentoId] = atendimentoId.Value.ToString();
            }

            foreach (var extra in extras)
            {
                if (extra.Value == null)
                {
                    dados.Remove(extra.Key);
                }
                else
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
                   string.Equals(estado, EstadoAguardandoNome, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(estado, EstadoAguardandoServico, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(estado, EstadoAguardandoVeiculo, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(estado, EstadoOfertaDetalhes, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(estado, EstadoAguardandoMarca, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(estado, EstadoAguardandoConfirmacaoFinal, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(estado, EstadoProntoAgendamento, StringComparison.OrdinalIgnoreCase) ||
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
                UsuarioPerguntouMarcasPeca(mensagemTexto) ||
                UsuarioPerguntouDetalhesServico(mensagemTexto) ||
                EhMensagemEncerramento(mensagemTexto) ||
                FoiPedidoHumano(mensagemTexto))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(ObterTexto(atendimentoAtual.DadosExtras, ChaveVehicleNome)))
            {
                return true;
            }

            if (ObterBool(atendimentoAtual.DadosExtras, ChaveUsuarioPerguntouPreco) ||
                ObterBool(atendimentoAtual.DadosExtras, ChaveUsuarioPerguntouDuracao) ||
                !string.IsNullOrWhiteSpace(ObterTexto(atendimentoAtual.DadosExtras, ChaveMarcaPecaNome)))
            {
                return mensagemTexto.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 10;
            }

            return mensagemTexto.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 10 ||
                   ContemAlgum(mensagemTexto, "como funciona", "me explica", "detalhes", "serve para", "esse servico", "esse serviço");
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

        private static string BuildResumoDoQueJaSei(
            Cliente? cliente,
            ServicoAtendimento? atendimentoAtual,
            ServicoCatalogItem? servico,
            ServicoCatalogVehicleItem? vehicle,
            ServicoCatalogPieceItem? marcaPeca,
            bool mencionarPendenciaVeiculo = false)
        {
            var partes = new List<string>();
            var primeiroNome = ObterPrimeiroNome(cliente?.Nome ?? atendimentoAtual?.NomeCliente);
            if (!string.IsNullOrWhiteSpace(primeiroNome))
            {
                partes.Add($"seu nome e {primeiroNome}");
            }

            if (servico != null)
            {
                partes.Add($"voce quer {servico.Nome}");
            }

            if (vehicle != null && !string.IsNullOrWhiteSpace(vehicle.NomeExibicao))
            {
                partes.Add($"o veiculo informado e {vehicle.NomeExibicao}");
            }

            if (marcaPeca != null && !string.IsNullOrWhiteSpace(marcaPeca.Nome))
            {
                partes.Add($"a marca anotada e {marcaPeca.Nome}");
            }

            if (partes.Count == 0)
            {
                return mencionarPendenciaVeiculo
                    ? "Ainda nao identifiquei o modelo do veiculo."
                    : string.Empty;
            }

            var resumo = partes.Count switch
            {
                1 => $"Ja sei que {partes[0]}",
                2 => $"Ja sei que {partes[0]} e que {partes[1]}",
                _ => $"Ja sei que {string.Join(", ", partes.Take(partes.Count - 1))} e que {partes[^1]}"
            };

            if (mencionarPendenciaVeiculo)
            {
                resumo = $"{resumo}, mas ainda nao identifiquei o modelo do veiculo";
            }

            return $"{resumo}.";
        }

        private static string BuildServiceReadyReply(ServicoCatalogItem servico, ServicoCatalogVehicleItem? vehicle)
        {
            return vehicle == null
                ? $"Entendi. Fazemos {servico.Nome}."
                : $"Entendi. Para {vehicle.NomeExibicao}, fazemos {servico.Nome}.";
        }

        private static string BuildServiceDetailsText(ServicoCatalogItem servico, ServicoCatalogVehicleItem? vehicle)
        {
            if (string.IsNullOrWhiteSpace(servico.Descricao))
            {
                return BuildServiceReadyReply(servico, vehicle);
            }

            var descricao = GarantirPontoFinal(TrimSentence(servico.Descricao!, 180));
            return vehicle == null
                ? $"Sobre {servico.Nome}: {descricao}"
                : $"Sobre {servico.Nome} para {vehicle.NomeExibicao}: {descricao}";
        }

        private static string? BuildFollowUpPrompt(
            ServicoCatalogItem servico,
            ServicoCatalogVehicleItem? vehicle,
            bool priceHandled,
            bool durationHandled)
        {
            var opcoes = new List<string>();
            if (!priceHandled)
            {
                opcoes.Add("valor");
            }

            if (!durationHandled)
            {
                opcoes.Add("tempo");
            }

            if (vehicle != null && vehicle.MarcasPeca.Count > 0)
            {
                opcoes.Add("marcas disponiveis");
            }

            if (opcoes.Count == 0)
            {
                return servico.PermiteAgendamento
                    ? "Quando fecharmos essa parte, eu sigo com voce para o proximo passo."
                    : null;
            }

            return $"Se quiser, eu tambem posso te passar {FormatarListaCurta(opcoes)}.";
        }

        private static string BuildBrandsReply(
            ServicoCatalogItem servico,
            ServicoCatalogVehicleItem? vehicle,
            ServicoCatalogPieceItem? marcaSelecionada)
        {
            if (vehicle == null)
            {
                return $"Ja entendi que voce quer {servico.Nome}. Agora eu so preciso do modelo do veiculo para te dizer as marcas disponiveis.";
            }

            if (vehicle.MarcasPeca.Count == 0)
            {
                return $"Para {servico.Nome} no {vehicle.NomeExibicao}, eu ainda nao tenho marcas de peca cadastradas no catalogo.";
            }

            if (marcaSelecionada != null)
            {
                return $"Perfeito. Para {servico.Nome} no {vehicle.NomeExibicao}, eu anotei a marca {marcaSelecionada.Nome}.";
            }

            var marcas = vehicle.MarcasPeca
                .Select(item => item.Nome)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToArray();

            return $"Para {servico.Nome} no {vehicle.NomeExibicao}, hoje eu tenho estas marcas:\n{FormatarOpcoesEnumeradas(marcas)}";
        }

        private static string? BuildPiecePriceText(
            ServicoCatalogItem servico,
            ServicoCatalogVehicleItem? vehicle,
            ServicoCatalogPieceItem marcaPeca)
        {
            if (vehicle == null)
            {
                return null;
            }

            var faixa = ObterFaixaPrecoMarcaPeca(marcaPeca);
            if (!faixa.Min.HasValue || !faixa.Max.HasValue)
            {
                return null;
            }

            if (faixa.Min.Value == faixa.Max.Value)
            {
                return $"Para {servico.Nome} no {vehicle.NomeExibicao}, com a marca {marcaPeca.Nome}, fica em {FormatCurrency(faixa.Min.Value)}.";
            }

            return $"Para {servico.Nome} no {vehicle.NomeExibicao}, com a marca {marcaPeca.Nome}, eu tenho uma faixa de {FormatCurrency(faixa.Min.Value)} a {FormatCurrency(faixa.Max.Value)}.";
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
                    ? $"Hoje esse servico fica em {FormatCurrency(valor.Value)}."
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

        private static (long? Min, long? Max) ObterFaixaPrecoMarcaPeca(ServicoCatalogPieceItem marcaPeca)
        {
            var candidatos = new[] { marcaPeca.ValorCentavos, marcaPeca.ValorMinimoCentavos, marcaPeca.ValorMaximoCentavos }
                .Where(item => item.HasValue)
                .Select(item => item!.Value)
                .ToArray();

            if (candidatos.Length == 0)
            {
                return (null, null);
            }

            return (candidatos.Min(), candidatos.Max());
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

        private static string GarantirPontoFinal(string texto)
        {
            if (string.IsNullOrWhiteSpace(texto))
            {
                return string.Empty;
            }

            var ultimo = texto[^1];
            return ultimo is '.' or '!' or '?'
                ? texto
                : $"{texto}.";
        }

        private static string ObterPrimeiroNome(string? nomeCompleto)
        {
            if (string.IsNullOrWhiteSpace(nomeCompleto))
            {
                return string.Empty;
            }

            return nomeCompleto
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()
                ?.Trim() ?? string.Empty;
        }

        private static string FormatarOpcoesEnumeradas(IReadOnlyList<string> opcoes)
        {
            return string.Join("\n", opcoes
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Take(5)
                .Select((item, index) => $"{index + 1}. {item.Trim()}"));
        }

        private static string FormatarListaCurta(IReadOnlyList<string> itens)
        {
            var valores = itens.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return valores.Length switch
            {
                0 => string.Empty,
                1 => valores[0],
                2 => $"{valores[0]} e {valores[1]}",
                _ => $"{string.Join(", ", valores.Take(valores.Length - 1))} e {valores[^1]}"
            };
        }

        private static ServicoCatalogPieceItem? ResolverMarcaPecaSelecionada(
            string mensagemTexto,
            ServicoCatalogVehicleItem vehicle,
            ServicoAtendimento? atendimentoAtual)
        {
            if (vehicle.MarcasPeca.Count == 0)
            {
                return null;
            }

            var nomes = vehicle.MarcasPeca
                .Select(item => item.Nome)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var escolhido = ResolverOpcaoPorTextoOuIndice(mensagemTexto, nomes, string.Empty);
            if (!string.IsNullOrWhiteSpace(escolhido))
            {
                return vehicle.MarcasPeca.FirstOrDefault(item => string.Equals(item.Nome, escolhido, StringComparison.OrdinalIgnoreCase));
            }

            var marcaPersistida = ObterTexto(atendimentoAtual?.DadosExtras, ChaveMarcaPecaNome);
            return string.IsNullOrWhiteSpace(marcaPersistida)
                ? null
                : vehicle.MarcasPeca.FirstOrDefault(item => string.Equals(item.Nome, marcaPersistida, StringComparison.OrdinalIgnoreCase));
        }

        private static EstabelecimentoCarro? ResolverVeiculoCatalogo(
            string mensagemTexto,
            IReadOnlyList<EstabelecimentoCarro> veiculos)
        {
            if (string.IsNullOrWhiteSpace(mensagemTexto) || veiculos.Count == 0)
            {
                return null;
            }

            var normalizado = NormalizeText(mensagemTexto);
            var tokensMensagem = Tokenize(normalizado);

            var candidatos = veiculos
                .Select(item =>
                {
                    var nomeCompleto = NormalizeText($"{item.Marca} {item.Modelo}");
                    var modelo = NormalizeText(item.Modelo);
                    var score = 0;

                    if (normalizado == nomeCompleto || normalizado == modelo)
                    {
                        score += 200;
                    }

                    if (nomeCompleto.Contains(normalizado, StringComparison.Ordinal) ||
                        modelo.Contains(normalizado, StringComparison.Ordinal) ||
                        normalizado.Contains(nomeCompleto, StringComparison.Ordinal))
                    {
                        score += 120;
                    }

                    var overlap = Tokenize(nomeCompleto).Count(token => tokensMensagem.Contains(token));
                    score += overlap * 25;

                    return new { Veiculo = item, Score = score };
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Veiculo.Marca)
                .ThenBy(item => item.Veiculo.Modelo)
                .ToArray();

            if (candidatos.Length == 0 || candidatos[0].Score < 45)
            {
                return null;
            }

            return candidatos[0].Veiculo;
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

        private async Task<ServicoCatalogVehicleItem?> ResolverVeiculoSelecionadoAsync(
            Guid idEstabelecimento,
            string mensagemTexto,
            ServicoCatalogItem servico,
            ConversationContext? contextoAtual)
        {
            var options = ObterListaTexto(contextoAtual?.DadosColetados, ChaveVehicleOptions);
            var escolhido = ResolverOpcaoPorTextoOuIndice(mensagemTexto, options, "servicos_veh_");
            if (!string.IsNullOrWhiteSpace(escolhido))
            {
                return servico.Veiculos.FirstOrDefault(item => string.Equals(item.NomeExibicao, escolhido, StringComparison.OrdinalIgnoreCase));
            }

            var veiculoDireto = servico.Veiculos
                .Where(item => !string.IsNullOrWhiteSpace(item.NomeExibicao))
                .Select(item => new { Vehicle = item, Nome = NormalizeText(item.NomeExibicao) })
                .FirstOrDefault(item =>
                {
                    var normalizado = NormalizeText(mensagemTexto);
                    return normalizado.Contains(item.Nome, StringComparison.Ordinal) || item.Nome.Contains(normalizado, StringComparison.Ordinal);
                });

            if (veiculoDireto != null)
            {
                return veiculoDireto.Vehicle;
            }

            var veiculosEstabelecimento = await _catalogProvider.ObterVeiculosAtivosAsync(idEstabelecimento);
            var veiculoCatalogo = ResolverVeiculoCatalogo(mensagemTexto, veiculosEstabelecimento);
            if (veiculoCatalogo == null)
            {
                return null;
            }

            var configurado = servico.Veiculos.FirstOrDefault(item => item.CarroId == veiculoCatalogo.Id);
            if (configurado != null)
            {
                return configurado;
            }

            return new ServicoCatalogVehicleItem
            {
                CarroId = veiculoCatalogo.Id,
                NomeExibicao = $"{veiculoCatalogo.Marca} {veiculoCatalogo.Modelo}".Trim(),
                Compativel = false
            };
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
                var configurado = servico.Veiculos.FirstOrDefault(item => item.CarroId == vehicleId.Value);
                if (configurado != null)
                {
                    return configurado;
                }

                var nomePersistido = ObterTexto(atendimentoAtual?.DadosExtras, ChaveVehicleNome);
                return new ServicoCatalogVehicleItem
                {
                    CarroId = vehicleId.Value,
                    NomeExibicao = string.IsNullOrWhiteSpace(nomePersistido) ? "veiculo informado" : nomePersistido!,
                    Compativel = false
                };
            }

            var vehicleNome = ObterTexto(atendimentoAtual?.DadosExtras, ChaveVehicleNome);
            return string.IsNullOrWhiteSpace(vehicleNome)
                ? null
                : servico.Veiculos.FirstOrDefault(item => string.Equals(item.NomeExibicao, vehicleNome, StringComparison.OrdinalIgnoreCase))
                  ?? new ServicoCatalogVehicleItem
                  {
                      NomeExibicao = vehicleNome!,
                      Compativel = false
                  };
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

        private static bool UsuarioPerguntouMarcasPeca(string texto)
        {
            return ContemAlgum(
                texto,
                "quais marcas",
                "qual marca",
                "marcas disponiveis",
                "marca da peca",
                "marca da peça",
                "quais marcas voces trabalham",
                "quais marcas vcs trabalham",
                "trabalham com qual marca");
        }

        private static bool UsuarioPerguntouDetalhesServico(string texto)
        {
            return ContemAlgum(
                texto,
                "como funciona",
                "me explica",
                "mais detalhes",
                "detalhes",
                "o que inclui",
                "como e feito",
                "como é feito");
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
