// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
// ✨ MUDANÇAS PRINCIPAIS:
// 1. Adicionados logs [HISTORICO-DEBUG] para rastrear busca e compactação de histórico

using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using APIBack.Automation.Helpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Message = APIBack.Automation.Models.Message;

namespace APIBack.Automation.Services
{
    public class ConversationProcessor
    {
        private static readonly System.Text.RegularExpressions.Regex PessoasRegex =
            new(@"(\d{1,3})\s*pessoas?", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static readonly System.Text.RegularExpressions.Regex HoraRegex =
            new(@"(\d{1,2}):(\d{2})", System.Text.RegularExpressions.RegexOptions.Compiled);

        private readonly ConversationService _conversationService;
        private readonly IQueueBus _fila;
        private readonly IWabaPhoneRepository _wabaRepo;
        private readonly IIARegraRepository _regrasRepo;
        private readonly IEstabelecimentoRepository _estabelecimentoRepo;
        private readonly IMessageRepository _mensagemRepository;
        private readonly PromptAssembler _promptAssembler;
        private readonly CentralRoutingService _centralRouting;
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
            IMessageRepository mensagemRepository,
            PromptAssembler promptAssembler,
            CentralRoutingService centralRouting,
            IMemoryCache cache,
            ILogger<ConversationProcessor> logger)
        {
            _conversationService = conversationService;
            _fila = fila;
            _wabaRepo = wabaRepo;
            _regrasRepo = regrasRepo;
            _estabelecimentoRepo = estabelecimentoRepo;
            _mensagemRepository = mensagemRepository;
            _promptAssembler = promptAssembler;
            _centralRouting = centralRouting;
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

            var criada = await _conversationService.AcrescentarEntradaAsync(
                idWa: input.Mensagem!.De!,
                idMensagemWa: input.Mensagem.Id!,
                conteudo: input.Texto,
                displayPhoneNumber: input.PhoneNumberDisplay ?? string.Empty,
                phoneNumberId: input.PhoneNumberId,
                dataMensagemUtc: input.DataMensagemUtc,
                tipoOrigem: input.Mensagem.Tipo,
                telefoneContato: telefoneNormalizado
            );

            if (criada == null)
            {
                _logger.LogInformation("[Webhook] Entrada ignorada apos verificacao de duplicidade. From={From}", input.Mensagem.De);
                return new ConversationProcessingResult(true, null, null, Array.Empty<AssistantChatTurn>(), null, new HandoverContextDto(), textoUsuario, input.PhoneNumberDisplay, input.PhoneNumberId);
            }

            criada.CriadaPor ??= "cliente";
            await _fila.PublicarEntradaAsync(criada);

            var tarefaContexto = ObterContextoAsync(criada.IdConversa, input.PhoneNumberDisplay);
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

        private async Task<string?> ObterContextoAsync(Guid idConversa, string? phoneNumberDisplay)
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
                    idEstabelecimento = await ResolverEstabelecimentoAsync(phoneNumberDisplay);
                }

                if (!idEstabelecimento.HasValue)
                {
                    return null;
                }

                var tipoEstabelecimento = await _estabelecimentoRepo.ObterTipoEstabelecimentoAsync(idEstabelecimento.Value);
                if (string.Equals(tipoEstabelecimento, "garagem", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tipoEstabelecimento, "nautica", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "[Conversa={Conversa}] Estabelecimento {Tipo} detectado; contexto de IA suprimido",
                        idConversa, tipoEstabelecimento);
                    return null;
                }

                var modulosAtivos = await DeterminarModulosAtivosAsync(idEstabelecimento.Value);

                var promptsCacheKey = $"prompts:{idEstabelecimento.Value}:{string.Join(",", modulosAtivos.OrderBy(m => m))}";
                if (!_cache.TryGetValue(promptsCacheKey, out string? promptMontado))
                {
                    var prompts = await _regrasRepo.ObterPromptsCompostosAsync(idEstabelecimento.Value, modulosAtivos);
                    promptMontado = _promptAssembler.Assemble(prompts);
                    _cache.Set(promptsCacheKey, promptMontado, PromptsCacheOptions);
                }

                if (string.Equals(tipoEstabelecimento, "oficina", StringComparison.OrdinalIgnoreCase))
                {
                    var nomeEstabelecimento = await _estabelecimentoRepo.ObterNomeFantasiaAsync(idEstabelecimento.Value);
                    return BuildOficinaPrompt(nomeEstabelecimento, promptMontado);
                }

                return promptMontado;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao carregar contexto para conversa {Conversa}", idConversa);
                return null;
            }
        }

        private static string BuildOficinaPrompt(string? nomeEstabelecimento, string? promptMontado)
        {
            var nome = string.IsNullOrWhiteSpace(nomeEstabelecimento) ? "Citrocar" : nomeEstabelecimento.Trim();
            var basePrompt =
                $"Voce representa a equipe da {nome}.\n\n" +
                "Objetivo:\n" +
                "- atender em portugues do Brasil\n" +
                "- responder de forma curta, cordial e objetiva\n" +
                "- priorizar FAQ simples da oficina\n\n" +
                "Formato:\n" +
                "- prefira responder sempre em JSON valido no formato {\"acao\":\"responder\",\"reply\":\"...\"}\n\n" +
                "Regras:\n" +
                "- se o nome do cliente ainda nao estiver conhecido, cumprimente e peca o nome antes de seguir\n" +
                "- fale em nome da equipe, sem se apresentar como assistente virtual\n" +
                "- nao faca diagnostico tecnico\n" +
                "- nao gere orcamento\n" +
                "- nao confirme preco, prazo ou agendamento automaticamente\n" +
                "- responda FAQ simples sobre servicos, contato, horario, endereco e como funciona o agendamento\n" +
                "- quando o assunto exigir equipe humana, avise de forma curta que a equipe vai continuar o atendimento por aqui\n" +
                "- nao faca questionario tecnico nem peca placa, quilometragem, urgencia, guincho ou dados parecidos por padrao";

            if (string.IsNullOrWhiteSpace(promptMontado))
            {
                return basePrompt;
            }

            return $"{basePrompt}\n\nInformacoes especificas do estabelecimento:\n{promptMontado}";
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

        private async Task<Guid?> ResolverEstabelecimentoAsync(string? phoneNumberDisplay)
        {
            if (string.IsNullOrWhiteSpace(phoneNumberDisplay))
            {
                return null;
            }

            var sanitized = SanitizarNumero(phoneNumberDisplay);
            Guid? idEstabelecimento = null;

            if (!string.IsNullOrWhiteSpace(sanitized))
            {
                idEstabelecimento = await _wabaRepo.ObterIdEstabelecimentoPorPhoneNumberIdAsync(sanitized);
            }

            if (!idEstabelecimento.HasValue || idEstabelecimento == Guid.Empty)
            {
                idEstabelecimento = await _wabaRepo.ObterIdEstabelecimentoPorPhoneNumberIdAsync(phoneNumberDisplay);
            }

            return (idEstabelecimento.HasValue && idEstabelecimento != Guid.Empty) ? idEstabelecimento : null;
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
