// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
// ✨ MUDANÇAS PRINCIPAIS:
// 1. Adicionados logs [HISTORICO-DEBUG] para rastrear busca e compactação de histórico

using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using APIBack.Automation.Helpers;
using APIBack.DTOs.Agendamentos;
using APIBack.Service.Interface;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Message = APIBack.Automation.Models.Message;

namespace APIBack.Automation.Services
{
    public class ConversationProcessor
    {
        private const string AvisoReinicioPorExpiracao = "Seu atendimento anterior expirou por inatividade, entao reiniciei nossa conversa por aqui.";

        private static string MontarAvisoEncerramentoManual(DateTime dataFechamento)
        {
            var diff = DateTime.UtcNow - dataFechamento;
            string quando;
            if (diff.TotalMinutes < 2)
                quando = "agora há pouco";
            else if (diff.TotalMinutes < 60)
                quando = $"há {(int)diff.TotalMinutes} minutos";
            else if (diff.TotalHours < 2)
                quando = "há cerca de 1 hora";
            else if (diff.TotalHours < 24)
                quando = $"há {(int)diff.TotalHours} horas";
            else if (diff.TotalDays < 2)
                quando = "ontem";
            else if (diff.TotalDays < 7)
                quando = $"há {(int)diff.TotalDays} dias";
            else
                quando = $"em {dataFechamento.ToLocalTime():dd/MM/yyyy}";

            return $"Olá! Identifiquei que você encerrou nosso atendimento {quando}. Caso queira iniciar uma nova conversa, é só continuar por aqui!";
        }
        private static readonly System.Text.RegularExpressions.Regex PessoasRegex =
            new(@"(\d{1,3})\s*pessoas?", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static readonly System.Text.RegularExpressions.Regex HoraRegex =
            new(@"(\d{1,2}):(\d{2})", System.Text.RegularExpressions.RegexOptions.Compiled);

        private readonly ConversationService _conversationService;
        private readonly IQueueBus _fila;
        private readonly IWabaPhoneRepository _wabaRepo;
        private readonly IIARegraRepository _regrasRepo;
        private readonly IEstabelecimentoRepository _estabelecimentoRepo;
        private readonly IConversationRepository _conversationRepository;
        private readonly IClienteRepository _clienteRepository;
        private readonly IMessageRepository _mensagemRepository;
        private readonly PromptAssembler _promptAssembler;
        private readonly CentralRoutingService _centralRouting;
        private readonly ServicoCatalogProvider _servicoCatalogProvider;
        private readonly IServicoAtendimentoRepository _servicoAtendimentoRepository;
        private readonly FaqCatalogProvider _faqCatalogProvider;
        private readonly IEstabelecimentoAgendamentoConfigService _agendamentoConfigService;
        private readonly IAgendaDisponibilidadeService _agendaDisponibilidadeService;
        private readonly IMemoryCache _cache;
        private readonly ILogger<ConversationProcessor> _logger;

        private static readonly MemoryCacheEntryOptions ModulosCacheOptions = new()
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60),
            Priority = CacheItemPriority.Normal
        };

        private static readonly MemoryCacheEntryOptions PromptsCacheOptions = new()
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60),
            Priority = CacheItemPriority.Normal
        };

        public ConversationProcessor(
            ConversationService conversationService,
            IQueueBus fila,
            IWabaPhoneRepository wabaRepo,
            IIARegraRepository regrasRepo,
            IEstabelecimentoRepository estabelecimentoRepo,
            IConversationRepository conversationRepository,
            IClienteRepository clienteRepository,
            IMessageRepository mensagemRepository,
            PromptAssembler promptAssembler,
            CentralRoutingService centralRouting,
            ServicoCatalogProvider servicoCatalogProvider,
            IServicoAtendimentoRepository servicoAtendimentoRepository,
            FaqCatalogProvider faqCatalogProvider,
            IEstabelecimentoAgendamentoConfigService agendamentoConfigService,
            IAgendaDisponibilidadeService agendaDisponibilidadeService,
            IMemoryCache cache,
            ILogger<ConversationProcessor> logger)
        {
            _conversationService = conversationService;
            _fila = fila;
            _wabaRepo = wabaRepo;
            _regrasRepo = regrasRepo;
            _estabelecimentoRepo = estabelecimentoRepo;
            _conversationRepository = conversationRepository;
            _clienteRepository = clienteRepository;
            _mensagemRepository = mensagemRepository;
            _promptAssembler = promptAssembler;
            _centralRouting = centralRouting;
            _servicoCatalogProvider = servicoCatalogProvider;
            _servicoAtendimentoRepository = servicoAtendimentoRepository;
            _faqCatalogProvider = faqCatalogProvider;
            _agendamentoConfigService = agendamentoConfigService;
            _agendaDisponibilidadeService = agendaDisponibilidadeService;
            _cache = cache;
            _logger = logger;
        }

        public async Task<ConversationProcessingResult> ProcessAsync(ConversationProcessingInput input)
        {
            var textoUsuario = string.IsNullOrWhiteSpace(input.TextoInterpretado)
                ? input.Texto
                : input.TextoInterpretado!;

            if (MensagemDoSistema(input))
            {
                _logger.LogInformation("[Webhook] Ignorando mensagem automatica do sistema (from={From})", input.Mensagem.De);
                return new ConversationProcessingResult(true, null, null, Array.Empty<AssistantChatTurn>(), null, new HandoverContextDto(), textoUsuario, input.PhoneNumberDisplay, input.PhoneNumberId);
            }

            var telefoneNormalizado = NormalizarTelefoneContato(input.Mensagem?.De);

            var ingresso = await _conversationService.AcrescentarEntradaAsync(
                idWa: input.Mensagem!.De!,
                idMensagemWa: input.Mensagem.Id!,
                conteudo: input.Texto,
                displayPhoneNumber: input.PhoneNumberDisplay ?? string.Empty,
                phoneNumberId: input.PhoneNumberId,
                dataMensagemUtc: input.DataMensagemUtc,
                tipoOrigem: input.Mensagem.Tipo,
                telefoneContato: telefoneNormalizado
            );

            if (ingresso == null)
            {
                _logger.LogInformation("[Webhook] Entrada ignorada apos verificacao de duplicidade. From={From}", input.Mensagem.De);
                return new ConversationProcessingResult(true, null, null, Array.Empty<AssistantChatTurn>(), null, new HandoverContextDto(), textoUsuario, input.PhoneNumberDisplay, input.PhoneNumberId);
            }

            var criada = ingresso.Mensagem;
            criada.CriadaPor ??= "cliente";
            await _fila.PublicarEntradaAsync(criada);

            var tarefaContexto = ObterContextoAsync(criada.IdConversa, input.PhoneNumberDisplay, input.PhoneNumberId);
            var tarefaHistorico = ObterHistoricoAsync(criada.IdConversa);
            await Task.WhenAll(tarefaContexto, tarefaHistorico);
            var contexto = tarefaContexto.Result;
            var historico = tarefaHistorico.Result;
            var handoverDetalhes = MontarHandoverDetalhes(input, criada, historico, contexto);

            return new ConversationProcessingResult(
                ShouldIgnore: false,
                MensagemRegistrada: criada,
                IdConversa: criada.IdConversa,
                Historico: historico,
                Contexto: contexto,
                HandoverDetalhes: handoverDetalhes,
                TextoUsuario: textoUsuario,
                NumeroTelefoneExibicao: input.PhoneNumberDisplay,
                NumeroWhatsappId: input.PhoneNumberId,
                AvisoRespostaInicial: ingresso.ReiniciadaPorExpiracao
                    ? AvisoReinicioPorExpiracao
                    : ingresso.AposEncerramentoManualEm.HasValue
                        ? MontarAvisoEncerramentoManual(ingresso.AposEncerramentoManualEm.Value)
                        : null);
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

        private static string? NormalizarTelefoneContato(string? numero)
        {
            if (string.IsNullOrWhiteSpace(numero))
            {
                return null;
            }

            try
            {
                return TelefoneHelper.ToE164(numero);
            }
            catch
            {
                return SanitizarNumero(numero);
            }
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

        private async Task<string?> ObterContextoAsync(Guid idConversa, string? phoneNumberDisplay, string? phoneNumberId)
        {
            try
            {
                Guid? idEstabelecimento;
                if (_centralRouting.IsCentralDisplayPhone(phoneNumberDisplay))
                {
                    var selecao = await _centralRouting.ObterSelecaoAtualAsync(idConversa);
                    if (!selecao.HasSelection)
                    {
                        _logger.LogInformation(
                            "[Conversa={Conversa}] Numero central sem estabelecimento escolhido; contexto de IA suprimido",
                            idConversa);
                        return null;
                    }

                    idEstabelecimento = selecao.EstabelecimentoId;
                }
                else
                {
                    idEstabelecimento = await ResolverEstabelecimentoAsync(phoneNumberDisplay, phoneNumberId);
                }

                if (!idEstabelecimento.HasValue)
                {
                    return null;
                }

                var estabelecimentoId = idEstabelecimento.Value;
                var tipoEstabelecimento = await _estabelecimentoRepo.ObterTipoEstabelecimentoAsync(estabelecimentoId);
                if (string.Equals(tipoEstabelecimento, "garagem", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tipoEstabelecimento, "nautica", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "[Conversa={Conversa}] Estabelecimento {Tipo} detectado; contexto de IA suprimido",
                        idConversa, tipoEstabelecimento);
                    return null;
                }

                var modulosAtivos = await DeterminarModulosAtivosAsync(estabelecimentoId);

                var promptsCacheKey = $"prompts:{estabelecimentoId}:{string.Join(",", modulosAtivos.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))}";
                if (!_cache.TryGetValue(promptsCacheKey, out string? promptMontado))
                {
                    var prompts = await _regrasRepo.ObterPromptsCompostosAsync(estabelecimentoId, modulosAtivos);
                    promptMontado = _promptAssembler.Assemble(prompts);
                    _cache.Set(promptsCacheKey, promptMontado, PromptsCacheOptions);
                }

                var nomeEstabelecimento = await _estabelecimentoRepo.ObterNomeFantasiaAsync(estabelecimentoId);
                var contextoAtual = await _conversationRepository.ObterContextoAsync(idConversa);
                var fichaAtual = await BuildFichaAtualAsync(idConversa, estabelecimentoId, modulosAtivos, contextoAtual);

                var secoes = new List<string>
                {
                    BuildAssistantProtocolPrompt(nomeEstabelecimento, tipoEstabelecimento, modulosAtivos, fichaAtual)
                };

                if (!string.IsNullOrWhiteSpace(promptMontado))
                {
                    secoes.Add($"Regras compostas do banco (IA_regras):\n{promptMontado}");
                }

                if (modulosAtivos.Any(item => string.Equals(item, "Servicos", StringComparison.OrdinalIgnoreCase)))
                {
                    var resumoServicos = await _servicoCatalogProvider.BuildCompactPromptAsync(estabelecimentoId);
                    if (!string.IsNullOrWhiteSpace(resumoServicos))
                    {
                        secoes.Add(resumoServicos);
                    }
                }

                if (modulosAtivos.Any(item => string.Equals(item, "FAQ", StringComparison.OrdinalIgnoreCase)))
                {
                    var resumoFaq = await _faqCatalogProvider.BuildCompactPromptAsync(estabelecimentoId);
                    if (!string.IsNullOrWhiteSpace(resumoFaq))
                    {
                        secoes.Add(resumoFaq);
                    }
                }

                if (modulosAtivos.Any(item =>
                    string.Equals(item, "Disponibilidade", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item, "Agendamentos", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item, "Reserva", StringComparison.OrdinalIgnoreCase)))
                {
                    var resumoDisponibilidade = await BuildAvailabilityPromptAsync(estabelecimentoId);
                    if (!string.IsNullOrWhiteSpace(resumoDisponibilidade))
                    {
                        secoes.Add(resumoDisponibilidade);
                    }
                }

                return string.Join("\n\n", secoes.Where(item => !string.IsNullOrWhiteSpace(item)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao carregar contexto para conversa {Conversa}", idConversa);
                return null;
            }
        }

        private string BuildAssistantProtocolPrompt(
            string? nomeEstabelecimento,
            string? tipoEstabelecimento,
            IReadOnlyCollection<string> modulosAtivos,
            ConversationFichaAtual fichaAtual)
        {
            var nome = string.IsNullOrWhiteSpace(nomeEstabelecimento) ? "o estabelecimento" : nomeEstabelecimento.Trim();
            var tipo = string.IsNullOrWhiteSpace(tipoEstabelecimento) ? "geral" : tipoEstabelecimento.Trim();
            var modulos = modulosAtivos.Count == 0
                ? "nenhum"
                : string.Join(", ", modulosAtivos.OrderBy(item => item, StringComparer.OrdinalIgnoreCase));

            var builder = new StringBuilder();
            builder.AppendLine($"Voce e a assistente virtual de atendimento via WhatsApp do estabelecimento {nome}.");
            builder.AppendLine($"Tipo de estabelecimento: {tipo}.");
            builder.AppendLine($"Modulos ativos neste atendimento: {modulos}.");
            builder.AppendLine();
            builder.AppendLine("Regras absolutas de memoria e continuidade:");
            builder.AppendLine("- leia o historico inteiro antes de responder");
            builder.AppendLine("- use a ficha_atual como memoria operacional da conversa");
            builder.AppendLine("- construa incrementalmente e nunca recomece do zero");
            builder.AppendLine("- se faltar apenas uma informacao, peca apenas essa informacao");
            builder.AppendLine("- se o cliente corrigir um dado, use o valor mais recente");
            builder.AppendLine("- mostre que voce lembra do que o cliente ja informou");
            builder.AppendLine("- mantenha contexto mesmo quando o cliente mudar de assunto e depois voltar");
            builder.AppendLine("- nunca invente preco, marca, prazo, disponibilidade ou qualquer outro dado que nao esteja no prompt, no historico ou no resultado de tools");
            builder.AppendLine();
            if (nome.Contains("Citrocar", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine("Regra especifica de abertura para a Citrocar:");
                builder.AppendLine("- no inicio de um novo atendimento, apresente-se como Citrocar e depois pergunte como pode ajudar");
                builder.AppendLine("- use preferencialmente: \"Ola! Voce esta falando com a Citrocar. Como posso ajuda-lo hoje?\"");
                builder.AppendLine("- nao peca o nome na primeira frase; so peca depois se ainda for necessario para avancar");
                builder.AppendLine();
            }
            builder.AppendLine("Regras de atendimento para o modulo Servicos:");
            builder.AppendLine("- voce e uma assistente de atendimento comercial, nao uma explicadora de mecanica");
            builder.AppendLine("- quando o cliente pedir um servico, garanta primeiro o nome do cliente");
            builder.AppendLine("- depois, se faltar veiculo, peca marca e modelo do veiculo");
            builder.AppendLine("- quando o cliente pedir detalhes, priorize valor, marcas aplicaveis, tempo e proximo passo");
            builder.AppendLine("- se o catalogo nao tiver algum dado, deixe isso claro e ofereca continuidade com a equipe sem inventar");
            builder.AppendLine("- so ofereca seguir para agendamento quando servico, veiculo e marca aplicavel estiverem definidos");
            builder.AppendLine();
            builder.AppendLine("Regras de atendimento para o modulo FAQ:");
            builder.AppendLine("- se o cliente fizer uma pergunta objetiva de FAQ, use a tool consultar_faq");
            builder.AppendLine("- FAQ pode interromper temporariamente qualquer outro assunto, inclusive servicos");
            builder.AppendLine("- ao responder um FAQ no meio de outro atendimento, preserve a ficha_atual existente");
            builder.AppendLine("- nao limpe servico, veiculo, pendencias ou pronto_para_agendamento so porque respondeu um FAQ");
            builder.AppendLine("- responda de acordo com a resposta cadastrada no FAQ");
            builder.AppendLine("- se havia uma pendencia antes do FAQ, voce pode retomar em uma unica frase curta no final");
            builder.AppendLine("- se consultar_faq nao encontrar resposta forte, continue o atendimento normal sem inventar");
            builder.AppendLine();
            builder.AppendLine("Regras de uso de tools:");
            builder.AppendLine("- use as tools quando precisar consultar catalogo, FAQ, disponibilidade ou registrar interesse");
            builder.AppendLine("- trate o resultado das tools como fonte de verdade");
            builder.AppendLine("- se uma tool retornar ficha_atual_sugerida, reflita isso na ficha_atual final da sua resposta");
            builder.AppendLine();
            builder.AppendLine("Formato de saida:");
            builder.AppendLine("- responda sempre em JSON valido");
            builder.AppendLine("- para respostas normais, prefira o formato:");
            builder.AppendLine("{\"acao\":\"responder\",\"reply\":\"...\",\"ficha_atual\":{\"nome_cliente\":\"...\",\"modulo_em_foco\":\"...\",\"servico\":\"...\",\"veiculo_marca\":\"...\",\"veiculo_modelo\":\"...\",\"marca_peca\":\"...\",\"pendencias\":[\"...\"],\"pronto_para_agendamento\":false}}");
            builder.AppendLine("- a ficha_atual e opcional, mas quando houver novos dados relevantes voce deve atualiza-la");
            builder.AppendLine("- ao responder FAQ no meio de outro fluxo, normalmente preserve a ficha_atual atual sem trocar modulo_em_foco");
            builder.AppendLine("- mantenha compatibilidade com acoes especiais ja existentes, como confirmar_reserva e escalar_para_humano, quando fizer sentido");
            builder.AppendLine();
            builder.AppendLine("Ficha atual da conversa:");
            builder.AppendLine(ConversationFichaAtualStore.ToJson(fichaAtual));
            return builder.ToString().TrimEnd();
        }

        private async Task<ConversationFichaAtual> BuildFichaAtualAsync(
            Guid idConversa,
            Guid idEstabelecimento,
            IReadOnlyCollection<string> modulosAtivos,
            ConversationContext? contextoAtual)
        {
            var fichaPersistida = ConversationFichaAtualStore.Read(contextoAtual);
            var scope = await _centralRouting.ResolveEffectiveScopeAsync(idConversa);
            var cliente = scope != null && scope.IdCliente != Guid.Empty
                ? await _clienteRepository.ObterPorIdAsync(scope.IdCliente)
                : null;
            var atendimentoServico = await _servicoAtendimentoRepository.ObterPorConversaAsync(idConversa);

            var conhecida = new ConversationFichaAtual
            {
                NomeCliente = cliente?.Nome,
                ModuloEmFoco = ResolveModuloEmFoco(modulosAtivos, atendimentoServico, fichaPersistida),
                Servico = atendimentoServico?.NomeServico,
                MarcaPeca = PrimeiroTextoDisponivel(
                    atendimentoServico?.DadosExtras,
                    "servicos_marca_nome",
                    "marcaPeca",
                    "marca_peca",
                    "marca"),
                ProntoParaAgendamento = fichaPersistida?.ProntoParaAgendamento
            };

            PopularVeiculo(atendimentoServico?.DadosExtras, conhecida);

            var ficha = ConversationFichaAtualStore.Merge(fichaPersistida, conhecida);
            ficha.Pendencias = BuildPendenciasPadrao(ficha, atendimentoServico);
            ficha.ProntoParaAgendamento ??= false;

            if (string.IsNullOrWhiteSpace(ficha.ModuloEmFoco))
            {
                ficha.ModuloEmFoco = ResolveModuloEmFoco(modulosAtivos, atendimentoServico, fichaPersistida);
            }

            return ConversationFichaAtualStore.Normalize(ficha);
        }

        private async Task<string?> BuildAvailabilityPromptAsync(Guid idEstabelecimento)
        {
            try
            {
                var configuracao = await _agendamentoConfigService.ObterAsync(idEstabelecimento);
                var regras = await _agendaDisponibilidadeService.ListarTodasAsync(idEstabelecimento);

                var builder = new StringBuilder();
                builder.AppendLine("Resumo de disponibilidade/agendamento:");
                builder.AppendLine($"- agenda_ativa={(configuracao.AgendaAtiva ? "sim" : "nao")}");
                builder.AppendLine($"- exige_servico={(configuracao.ExigeServico ? "sim" : "nao")}");
                builder.AppendLine($"- exige_profissional={(configuracao.ExigeProfissional ? "sim" : "nao")}");
                builder.AppendLine($"- agenda_informativo={(configuracao.AgendaInformativo ? "sim" : "nao")}");

                foreach (var regra in regras
                    .Where(item => item.Ativo)
                    .Take(8))
                {
                    builder.Append("- ");
                    builder.Append(regra.Escopo);
                    builder.Append(" | ");
                    builder.Append(regra.Tipo);

                    if (regra.DiasSemana.Count > 0)
                    {
                        builder.Append(" | dias=");
                        builder.Append(string.Join(", ", regra.DiasSemana.Select(FormatarDiaSemana)));
                    }

                    if (regra.DataInicio.HasValue || regra.DataFim.HasValue)
                    {
                        builder.Append(" | periodo=");
                        builder.Append(regra.DataInicio?.ToString("dd/MM/yyyy"));
                        builder.Append(" a ");
                        builder.Append(regra.DataFim?.ToString("dd/MM/yyyy"));
                    }

                    if (regra.DiaInteiro)
                    {
                        builder.Append(" | dia_inteiro=sim");
                    }
                    else if (regra.HoraInicio.HasValue && regra.HoraFim.HasValue)
                    {
                        builder.Append(" | horario=");
                        builder.Append(regra.HoraInicio.Value.ToString("HH:mm"));
                        builder.Append("-");
                        builder.Append(regra.HoraFim.Value.ToString("HH:mm"));
                    }

                    builder.AppendLine();
                }

                return builder.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Falha ao montar resumo de disponibilidade para estabelecimento {Estabelecimento}", idEstabelecimento);
                return null;
            }
        }

        private static string ResolveModuloEmFoco(
            IReadOnlyCollection<string> modulosAtivos,
            ServicoAtendimento? atendimentoServico,
            ConversationFichaAtual? fichaPersistida)
        {
            if (!string.IsNullOrWhiteSpace(fichaPersistida?.ModuloEmFoco))
            {
                return fichaPersistida.ModuloEmFoco!;
            }

            if (atendimentoServico != null || !string.IsNullOrWhiteSpace(fichaPersistida?.Servico))
            {
                return "servicos";
            }

            if (modulosAtivos.Any(item => string.Equals(item, "FAQ", StringComparison.OrdinalIgnoreCase)))
            {
                return "faq";
            }

            if (modulosAtivos.Any(item =>
                string.Equals(item, "Disponibilidade", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item, "Agendamentos", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item, "Reserva", StringComparison.OrdinalIgnoreCase)))
            {
                return "agendamento";
            }

            return "atendimento";
        }

        private static List<string> BuildPendenciasPadrao(ConversationFichaAtual fichaAtual, ServicoAtendimento? atendimentoServico)
        {
            var pendencias = fichaAtual.Pendencias ?? new List<string>();
            if (string.IsNullOrWhiteSpace(fichaAtual.NomeCliente))
            {
                pendencias.Add("nome_cliente");
            }

            if (!string.IsNullOrWhiteSpace(fichaAtual.Servico) || !string.IsNullOrWhiteSpace(atendimentoServico?.NomeServico))
            {
                if (string.IsNullOrWhiteSpace(fichaAtual.VeiculoMarca))
                {
                    pendencias.Add("veiculo_marca");
                }

                if (string.IsNullOrWhiteSpace(fichaAtual.VeiculoModelo))
                {
                    pendencias.Add("veiculo_modelo");
                }
            }

            return pendencias
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void PopularVeiculo(IReadOnlyDictionary<string, object?>? dadosExtras, ConversationFichaAtual fichaAtual)
        {
            if (dadosExtras == null)
            {
                return;
            }

            var marca = PrimeiroTextoDisponivel(dadosExtras,
                "veiculo_marca",
                "marca_veiculo",
                "vehicle_brand");
            var modelo = PrimeiroTextoDisponivel(dadosExtras,
                "veiculo_modelo",
                "modelo_veiculo",
                "vehicle_model");
            var veiculoTexto = PrimeiroTextoDisponivel(dadosExtras,
                "veiculo",
                "servicos_veiculo_nome",
                "vehicle",
                "servicos_vehicle_name");

            if (string.IsNullOrWhiteSpace(marca) || string.IsNullOrWhiteSpace(modelo))
            {
                DecomporVeiculoLivre(veiculoTexto, out var marcaInferida, out var modeloInferido);
                marca ??= marcaInferida;
                modelo ??= modeloInferido;
            }

            fichaAtual.VeiculoMarca = marca;
            fichaAtual.VeiculoModelo = modelo;
        }

        private static string? PrimeiroTextoDisponivel(IReadOnlyDictionary<string, object?>? dados, params string[] chaves)
        {
            if (dados == null)
            {
                return null;
            }

            foreach (var chave in chaves)
            {
                if (!dados.TryGetValue(chave, out var raw) || raw == null)
                {
                    continue;
                }

                switch (raw)
                {
                    case string texto when !string.IsNullOrWhiteSpace(texto):
                        return texto.Trim();
                    case JsonElement element when element.ValueKind == JsonValueKind.String:
                    {
                        var value = element.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return value.Trim();
                        }

                        break;
                    }
                }
            }

            return null;
        }

        private static void DecomporVeiculoLivre(string? veiculoTexto, out string? marca, out string? modelo)
        {
            marca = null;
            modelo = null;

            if (string.IsNullOrWhiteSpace(veiculoTexto))
            {
                return;
            }

            var partes = veiculoTexto
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (partes.Length == 0)
            {
                return;
            }

            marca = partes[0];
            modelo = partes.Length == 1 ? null : string.Join(' ', partes.Skip(1));
        }

        private static string FormatarDiaSemana(int diaSemana)
        {
            return diaSemana switch
            {
                0 => "domingo",
                1 => "segunda",
                2 => "terca",
                3 => "quarta",
                4 => "quinta",
                5 => "sexta",
                6 => "sabado",
                _ => diaSemana.ToString()
            };
        }

        private async Task<IReadOnlyCollection<string>> DeterminarModulosAtivosAsync(Guid idEstabelecimento)
        {
            var cacheKey = $"modulos:{idEstabelecimento}";
            if (_cache.TryGetValue(cacheKey, out IReadOnlyCollection<string>? cached) && cached != null)
            {
                return cached;
            }

            try
            {
                var modulosAtivos = await _estabelecimentoRepo.ObterModulosAtivosAsync(idEstabelecimento);

                if (modulosAtivos.Count == 0)
                {
                    _logger.LogWarning("Nenhum modulo ativo encontrado para estabelecimento {Estabelecimento}", idEstabelecimento);
                    _cache.Set<IReadOnlyCollection<string>>(cacheKey, [], ModulosCacheOptions);
                    return Array.Empty<string>();
                }

                _logger.LogDebug("Modulos ativos encontrados para estabelecimento {Estabelecimento}: {Modulos}",
                    idEstabelecimento, string.Join(", ", modulosAtivos));

                _cache.Set(cacheKey, modulosAtivos, ModulosCacheOptions);
                return modulosAtivos;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao determinar modulos ativos para estabelecimento {Estabelecimento}", idEstabelecimento);
                return Array.Empty<string>();
            }
        }

        private async Task<Guid?> ResolverEstabelecimentoAsync(string? phoneNumberDisplay, string? phoneNumberId)
        {
            if (!string.IsNullOrWhiteSpace(phoneNumberDisplay))
            {
                var idPorDisplay = await _wabaRepo.ObterIdEstabelecimentoPorDisplayPhoneAsync(phoneNumberDisplay);
                if (idPorDisplay.HasValue && idPorDisplay.Value != Guid.Empty)
                {
                    return idPorDisplay.Value;
                }
            }

            if (!string.IsNullOrWhiteSpace(phoneNumberId))
            {
                var idPorPhoneNumberId = await _wabaRepo.ObterIdEstabelecimentoPorPhoneNumberIdAsync(phoneNumberId);
                if (idPorPhoneNumberId.HasValue && idPorPhoneNumberId.Value != Guid.Empty)
                {
                    return idPorPhoneNumberId.Value;
                }
            }

            return null;
        }

        private async Task<IReadOnlyList<AssistantChatTurn>> ObterHistoricoAsync(Guid idConversa)
        {
            try
            {
                var historico = await _mensagemRepository.GetByConversationAsync(idConversa, limit: 50);

                var turnos = historico
                    .Where(m => !string.IsNullOrWhiteSpace(m.Conteudo))
                    .Select(m => new AssistantChatTurn
                    {
                        Role = m.Direcao == DirecaoMensagem.Entrada ? "user" : "assistant",
                        Content = m.Conteudo,
                        Timestamp = m.DataHora
                    })
                    .ToList();

                if (turnos.Count > 20)
                {
                    var turnosRecentes = turnos.TakeLast(15).ToList();
                    var turnosAntigos = turnos.Take(turnos.Count - 15).ToList();
                    var resumo = CriarResumoCompacto(turnosAntigos);

                    var turnosCompactados = new List<AssistantChatTurn>
                    {
                        new AssistantChatTurn
                        {
                            Role = "assistant",
                            Content = $"[Resumo conversa anterior: {resumo}]",
                            Timestamp = turnosAntigos.First().Timestamp
                        }
                    };

                    turnosCompactados.AddRange(turnosRecentes);
                    return turnosCompactados;
                }

                return turnos;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao carregar historico para conversa {Conversa}", idConversa);
                return Array.Empty<AssistantChatTurn>();
            }
        }

        private string CriarResumoCompacto(List<AssistantChatTurn> turnos)
        {
            var dadosExtraidos = new List<string>();

            foreach (var turno in turnos)
            {
                if (turno.Role == "user" && !string.IsNullOrWhiteSpace(turno.Content))
                {
                    var conteudo = turno.Content.Trim();

                    if (conteudo.Length > 10 && !conteudo.Any(char.IsDigit) && conteudo.Split(' ').Length >= 2)
                    {
                        dadosExtraidos.Add($"Nome={conteudo.Substring(0, Math.Min(50, conteudo.Length))}");
                    }

                    var matchPessoas = PessoasRegex.Match(conteudo);
                    if (matchPessoas.Success)
                    {
                        dadosExtraidos.Add($"QtdPessoas={matchPessoas.Groups[1].Value}");
                    }

                    var matchHora = HoraRegex.Match(conteudo);
                    if (matchHora.Success)
                    {
                        dadosExtraidos.Add($"Horario={matchHora.Value}");
                    }
                }
            }

            return dadosExtraidos.Any()
                ? string.Join(", ", dadosExtraidos.Distinct())
                : "conversa inicial";
        }

        private static HandoverContextDto MontarHandoverDetalhes(ConversationProcessingInput input, Message mensagem, IReadOnlyList<AssistantChatTurn> historico, string? contexto)
        {
            var clienteNome = input.Valor.Contatos?.FirstOrDefault()?.Perfil?.Nome;
            return new HandoverContextDto
            {
                ClienteNome = string.IsNullOrWhiteSpace(clienteNome) ? mensagem.IdConversa.ToString() : clienteNome,
                Telefone = NormalizarTelefoneContato(input.Mensagem.De) ?? SanitizarNumero(input.Mensagem.De),
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
