// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using APIBack.Automation.Dtos;
using APIBack.Automation.Helpers;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using APIBack.DTOs;
using APIBack.Model;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace APIBack.Automation.Services
{
    /// <summary>
    /// Serviço responsável por interceptar mensagens quando há contexto de conversa ativo
    /// (ex: escolha de reserva, alteração de dados, confirmação)
    /// </summary>
    public class ContextInterceptorService
    {
        private readonly IConversationRepository _conversationRepository;
        private readonly APIBack.Repository.Interface.IReservaRepository _reservaRepository;
        private readonly IClienteRepository _clienteRepository;
        private readonly ILogger<ContextInterceptorService> _logger;
        private readonly ToolExecutorService _toolExecutor;
        private readonly CentralRoutingService _centralRouting;
        private readonly OficinaFlowService _oficinaFlow;
        private readonly GarageFlowService _garageFlow;
        private readonly NauticaFlowService _nauticaFlow;
        private readonly TopicOrchestratorService _topicOrchestrator;
        private readonly ServicosFlowService _servicosFlow;
        private readonly ConversationResetService _conversationReset;

        public ContextInterceptorService(
            IConversationRepository conversationRepository,
            APIBack.Repository.Interface.IReservaRepository reservaRepository,
            IClienteRepository clienteRepository,
            ILogger<ContextInterceptorService> logger,
            ToolExecutorService toolExecutor,
            CentralRoutingService centralRouting,
            OficinaFlowService oficinaFlow,
            GarageFlowService garageFlow,
            NauticaFlowService nauticaFlow,
            TopicOrchestratorService topicOrchestrator,
            ServicosFlowService servicosFlow,
            ConversationResetService conversationReset)
        {
            _conversationRepository = conversationRepository;
            _reservaRepository = reservaRepository;
            _clienteRepository = clienteRepository;
            _logger = logger;
            _toolExecutor = toolExecutor;
            _centralRouting = centralRouting;
            _oficinaFlow = oficinaFlow;
            _garageFlow = garageFlow;
            _nauticaFlow = nauticaFlow;
            _topicOrchestrator = topicOrchestrator;
            _servicosFlow = servicosFlow;
            _conversationReset = conversationReset;
        }

        private async Task<List<APIBack.Model.Reserva>> ObterReservasAtivasAsync(
            Guid idCliente,
            Guid idEstabelecimento,
            DateTime baseReferencia)
        {
            var reservasExistentes = await _reservaRepository.ObterPorClienteEstabelecimentoAsync(idCliente, idEstabelecimento);
            var referenciaAtual = baseReferencia;

            return reservasExistentes
                .Where(r =>
                {
                    if (r.Status != APIBack.Model.ReservaStatus.Confirmado) return false;
                    var dataHoraReserva = r.DataReserva.Date.Add(r.HoraInicio);
                    return dataHoraReserva > referenciaAtual;
                })
                .OrderBy(r => r.DataReserva)
                .ThenBy(r => r.HoraInicio)
                .ToList();
        }

        private async Task<(bool Intercepted, AssistantDecision? Decision)> TryHandleCentralRoutingAsync(
            Guid idConversa,
            string mensagemTexto,
            string? phoneNumberDisplay)
        {
            if (!_centralRouting.IsCentralDisplayPhone(phoneNumberDisplay))
            {
                return (false, null);
            }

            _logger.LogInformation("[Conversa={Conversa}] Numero central detectado", idConversa);

            var contexto = await _conversationRepository.ObterContextoAsync(idConversa);

            var selecao = await _centralRouting.ObterSelecaoAtualAsync(idConversa, contexto);
            if (selecao.SelectionExpired)
            {
                _logger.LogInformation(
                    "[Conversa={Conversa}] Escolha expirada; retornando ao menu inicial",
                    idConversa);
                return await ReenviarMenuCentralAsync(idConversa, reiniciado: false);
            }

            if (string.Equals(contexto?.Estado, CentralRoutingService.EstadoAguardandoEscolha, StringComparison.OrdinalIgnoreCase))
            {
                return await ProcessarEscolhaEstabelecimentoAsync(idConversa, mensagemTexto);
            }

            if (!selecao.HasSelection)
            {
                return await ReenviarMenuCentralAsync(idConversa, reiniciado: false);
            }

            if (string.Equals(contexto?.Estado, CentralRoutingService.EstadoEstabelecimentoSelecionado, StringComparison.OrdinalIgnoreCase))
            {
                await _centralRouting.RenovarSelecaoAsync(idConversa, contexto);
                _logger.LogDebug(
                    "[Conversa={Conversa}] Selecao central renovada para estabelecimento {Estabelecimento}",
                    idConversa,
                    selecao.EstabelecimentoId);
            }

            return (false, null);
        }

        private async Task<(bool Intercepted, AssistantDecision? Decision)> ReenviarMenuCentralAsync(
            Guid idConversa,
            bool reiniciado)
        {
            var estabelecimentos = await _centralRouting.ListarEstabelecimentosElegiveisAsync();
            var mensagem = _centralRouting.BuildSelectionMenuMessage(estabelecimentos, reiniciado);
            await _centralRouting.SalvarMenuEscolhaAsync(idConversa, estabelecimentos);
            await SalvarMensagemRespostaAsync(idConversa, mensagem);
            _logger.LogInformation(
                "[Conversa={Conversa}] Menu central enviado com {Count} opcoes",
                idConversa,
                estabelecimentos.Count);
            return (true, new AssistantDecision(mensagem, "none", null, false, null, null));
        }

        private async Task<(bool Intercepted, AssistantDecision? Decision)> ProcessarEscolhaEstabelecimentoAsync(
            Guid idConversa,
            string mensagemTexto)
        {
            var estabelecimentos = await _centralRouting.ListarEstabelecimentosElegiveisAsync();
            if (estabelecimentos.Count == 0)
            {
                var indisponivel = _centralRouting.BuildSelectionMenuMessage(estabelecimentos, false);
                await _centralRouting.SalvarMenuEscolhaAsync(idConversa, estabelecimentos);
                await SalvarMensagemRespostaAsync(idConversa, indisponivel);
                return (true, new AssistantDecision(indisponivel, "none", null, false, null, null));
            }

            var escolhido = _centralRouting.TryResolveEscolha(mensagemTexto, estabelecimentos, out var ambiguaPorNome);
            if (escolhido == null)
            {
                var respostaInvalida = _centralRouting.BuildSelectionInvalidMessage(estabelecimentos, ambiguaPorNome);
                await _centralRouting.SalvarMenuEscolhaAsync(idConversa, estabelecimentos);
                await SalvarMensagemRespostaAsync(idConversa, respostaInvalida);
                return (true, new AssistantDecision(respostaInvalida, "none", null, false, null, null));
            }

            var idConversaAtiva = await _centralRouting.SalvarEstabelecimentoSelecionadoAsync(idConversa, escolhido);

            var decisaoGaragem = await _garageFlow.TryStartAfterCentralSelectionAsync(idConversaAtiva);
            if (decisaoGaragem != null)
            {
                await SalvarMensagemRespostaAsync(idConversaAtiva, decisaoGaragem.Reply);
                _logger.LogInformation(
                    "[Conversa={Conversa}] Estabelecimento garagem escolhido no contexto: {Estabelecimento}",
                    idConversaAtiva,
                    escolhido.Id);
                return (true, decisaoGaragem);
            }

            var decisaoNautica = await _nauticaFlow.TryStartAfterCentralSelectionAsync(idConversaAtiva);
            if (decisaoNautica != null)
            {
                await SalvarMensagemRespostaAsync(idConversaAtiva, decisaoNautica.Reply);
                _logger.LogInformation(
                    "[Conversa={Conversa}] Estabelecimento nautica escolhido no contexto: {Estabelecimento}",
                    idConversaAtiva,
                    escolhido.Id);
                return (true, decisaoNautica);
            }

            var resposta = $"Perfeito. Vou continuar seu atendimento com {escolhido.Nome}.";
            await SalvarMensagemRespostaAsync(idConversaAtiva, resposta);
            _logger.LogInformation(
                "[Conversa={Conversa}] Estabelecimento escolhido no contexto: {Estabelecimento}",
                idConversaAtiva,
                escolhido.Id);
            return (true, new AssistantDecision(resposta, "none", null, false, null, null));
        }

        public async Task<(bool Intercepted, AssistantDecision? Decision)> TryHandleResetAsync(
            Guid idConversa,
            string mensagemTexto,
            string? phoneNumberDisplay)
        {
            if (!_conversationReset.IsResetCommand(mensagemTexto))
            {
                return (false, null);
            }

            var decision = await _conversationReset.ResetAndBuildReplyAsync(idConversa, phoneNumberDisplay);
            return (true, decision);
        }

        /// <summary>
        /// Verifica se há contexto ativo e intercepta a mensagem se necessário
        /// </summary>
        /// <returns>True se a mensagem foi interceptada e processada, False se deve seguir para IA</returns>
        public async Task<(bool Intercepted, AssistantDecision? Decision)> TryInterceptAsync(
            Guid idConversa,
            string mensagemTexto,
            DateTime? timestampMensagemUtc = null,
            string? phoneNumberDisplay = null)
        {
            var (resetIntercepted, resetDecision) = await TryHandleResetAsync(
                idConversa,
                mensagemTexto,
                phoneNumberDisplay);
            if (resetIntercepted)
            {
                return (true, resetDecision);
            }

            DateTime baseReferencia;
            if (timestampMensagemUtc.HasValue)
            {
                baseReferencia = TimeZoneHelper.ConvertUtcToSaoPaulo(timestampMensagemUtc.Value);
                _logger.LogDebug(
                    "[Conversa={Conversa}] Usando timestamp da mensagem: {Timestamp:yyyy-MM-dd HH:mm:ss} SP",
                    idConversa,
                    baseReferencia);
            }
            else
            {
                baseReferencia = TimeZoneHelper.GetSaoPauloNow();
                _logger.LogDebug(
                    "[Conversa={Conversa}] Usando horario atual do servidor: {Timestamp:yyyy-MM-dd HH:mm:ss} SP",
                    idConversa,
                    baseReferencia);
            }

            var (centralIntercepted, centralDecision) = await TryHandleCentralRoutingAsync(
                idConversa,
                mensagemTexto,
                phoneNumberDisplay);
            if (centralIntercepted)
            {
                return (true, centralDecision);
            }

            var (garageIntercepted, garageDecision) = await _garageFlow.TryHandleAsync(
                idConversa,
                mensagemTexto,
                phoneNumberDisplay);
            if (garageIntercepted)
            {
                return (true, garageDecision);
            }

            var (nauticaIntercepted, nauticaDecision) = await _nauticaFlow.TryHandleAsync(
                idConversa,
                mensagemTexto,
                phoneNumberDisplay);
            if (nauticaIntercepted)
            {
                return (true, nauticaDecision);
            }

            var (topicIntercepted, topicDecision) = await _topicOrchestrator.TryHandleAsync(
                idConversa,
                mensagemTexto,
                phoneNumberDisplay);
            if (topicIntercepted)
            {
                return (true, topicDecision);
            }

            var (servicosIntercepted, servicosDecision) = await _servicosFlow.TryHandleAsync(
                idConversa,
                mensagemTexto,
                phoneNumberDisplay);
            if (servicosIntercepted)
            {
                return (true, servicosDecision);
            }

            var (oficinaIntercepted, oficinaDecision) = await _oficinaFlow.TryHandleAsync(
                idConversa,
                mensagemTexto,
                phoneNumberDisplay);
            if (oficinaIntercepted)
            {
                return (true, oficinaDecision);
            }

            // ------- DETECÇÃO INTELIGENTE DE FILTROS -------

            // ═══════ INTERCEPTADOR DIRETO: "cancelar/alterar a reserva 1035" ═══════
            var textoParaIntercept = mensagemTexto?.ToLower().Trim() ?? "";

            // 1) Detectar "cancelar a reserva 1035"
            var matchCancelar = Regex.Match(textoParaIntercept, @"(?:quero\s+)?cancelar\s+(?:a\s+)?reserva\s+#?(\d{3,5})");
            if (matchCancelar.Success && long.TryParse(matchCancelar.Groups[1].Value, out var codigoCancelar))
            {
                _logger.LogInformation(
                    "[Conversa={Conversa}] ✅ INTERCEPTADO: 'cancelar a reserva {Codigo}' - chamando tool diretamente",
                    idConversa, codigoCancelar);

                var cancelarArgsObj = new CancelarReservaArgs
                {
                    IdConversa = idConversa,
                    CodigoReserva = codigoCancelar,
                    MotivoCliente = "Solicitação direta do cliente"
                };

                var cancelarJson = JsonSerializer.Serialize(cancelarArgsObj);
                var resultado = await _toolExecutor.ExecuteToolAsync("cancelar_reserva", cancelarJson);

                var respostaObj = JsonSerializer.Deserialize<Dictionary<string, object>>(resultado);
                var reply = respostaObj?["reply"]?.ToString() ?? "Reserva processada.";

                await SalvarMensagemRespostaAsync(idConversa, reply);
                return (true, new AssistantDecision(reply, "cancelar_reserva", null, false, null, null));
            }

            // 2) Detectar "alterar/mudar a reserva 1035"
            var matchAlterar = Regex.Match(textoParaIntercept, @"(?:quero\s+)?(?:alterar|mudar|modificar)\s+(?:a\s+)?reserva\s+#?(\d{3,5})");
            if (matchAlterar.Success && long.TryParse(matchAlterar.Groups[1].Value, out var codigoAlterar))
            {
                _logger.LogInformation(
                    "[Conversa={Conversa}] ✅ INTERCEPTADO: 'alterar a reserva {Codigo}' - chamando tool diretamente",
                    idConversa, codigoAlterar);

                var atualizarArgsObj = new AtualizarReservaArgs
                {
                    IdConversa = idConversa,
                    CodigoReserva = codigoAlterar
                };

                var atualizarJson = JsonSerializer.Serialize(atualizarArgsObj);
                var resultado = await _toolExecutor.ExecuteToolAsync("atualizar_reserva", atualizarJson);

                var respostaObj = JsonSerializer.Deserialize<Dictionary<string, object>>(resultado);
                var reply = respostaObj?["reply"]?.ToString() ?? "Reserva processada.";

                await SalvarMensagemRespostaAsync(idConversa, reply);
                return (true, new AssistantDecision(reply, "atualizar_reserva", null, false, null, null));
            }
            // ═══════ FIM INTERCEPTADOR DIRETO ═══════

            // ═══════ INTERCEPTADOR: PALAVRAS-CHAVE DE "OUTROS ASSUNTOS" ═══════
            var textoParaDeteccao = mensagemTexto?.ToLower().Trim() ?? "";

            var palavrasChaveOutrosAssuntos = new[]
            {
                // Cardápio
                "cardapio", "cardápio", "menu", "pratos", "comida", "comidas",
                "bebida", "bebidas", "drink", "drinks", "vinho", "vinhos",
                "sobremesa", "sobremesas", "entrada", "entradas",
                "prato do dia", "especialidade", "especialidades",
                "vegano", "vegetariano", "sem gluten", "sem glúten",

                // Localização
                "endereco", "endereço", "localizacao", "localização",
                "onde fica", "onde é", "onde vocês ficam",
                "como chegar", "como ir", "mapa", "rota",
                "perto de", "bairro", "rua", "cep",

                // Horário
                "horario", "horário", "abre", "fecha", "funciona",
                "aberto", "fechado", "que horas",
                "domingo", "segunda", "terca", "terça", "quarta", "quinta", "sexta", "sabado", "sábado",

                // Contato
                "telefone", "contato", "whatsapp", "email", "instagram",

                // Pagamento
                "pagamento", "pagar", "aceita", "cartao", "cartão", "pix", "dinheiro",

                // Estacionamento
                "estacionamento", "estacionar", "vaga", "vagas",

                // Eventos
                "evento", "promocao", "promoção", "desconto", "happy hour",

                // Gerais
                "disponibilidade", "lotado", "delivery", "quanto custa", "preco", "preço"
            };

            var temPalavraChave = palavrasChaveOutrosAssuntos.Any(palavra =>
                textoParaDeteccao.Contains(palavra));

            if (!temPalavraChave)
            {
                var padroesRegex = new[]
                {
                    @"qual\s+(o|a|e|é)\s+",
                    @"onde\s+(fica|e|é|esta|está)",
                    @"voce(s)?\s+tem",
                    @"voce(s)?\s+aceita",
                    @"quanto\s+(custa|é|e)",
                    @"como\s+(funciona|e|é)"
                };

                temPalavraChave = padroesRegex.Any(padrao =>
                    Regex.IsMatch(textoParaDeteccao, padrao, RegexOptions.IgnoreCase));
            }

            if (temPalavraChave)
            {
                var contextoAtual = await _conversationRepository.ObterContextoAsync(idConversa);

                if (contextoAtual != null && !string.IsNullOrWhiteSpace(contextoAtual.Estado))
                {
                    var estadosQuePodemPausar = new[]
                    {
                        "aguardando_escolha_acao",
                        "aguardando_escolha_reserva",
                        "aguardando_dados_alteracao",
                        "aguardando_confirmacao_alteracao"
                    };

                    if (estadosQuePodemPausar.Contains(contextoAtual.Estado))
                    {
                        _logger.LogInformation(
                            "[Conversa={Conversa}] 🔑 PALAVRA-CHAVE detectada: '{Mensagem}' - Pausando contexto '{Estado}'",
                            idConversa, mensagemTexto, contextoAtual.Estado);

                        var contextoPreservado = new ConversationContext
                        {
                            Estado = "pausado_para_outros_assuntos",
                            ReservaIdPendente = contextoAtual.ReservaIdPendente,
                            DadosColetados = contextoAtual.DadosColetados ?? new Dictionary<string, object>(),
                            ReservaSnapshot = contextoAtual.ReservaSnapshot,
                            ExpiracaoEstado = DateTime.UtcNow.AddHours(2)
                        };

                        contextoPreservado.DadosColetados["estado_anterior"] = contextoAtual.Estado;
                        contextoPreservado.DadosColetados["pausado_em"] = DateTime.UtcNow.ToString("o");

                        await _conversationRepository.SalvarContextoAsync(idConversa, contextoPreservado);

                        return (false, null);
                    }
                }
            }
            // ═══════ FIM INTERCEPTADOR PALAVRAS-CHAVE ═══════

            // ═══════ INTERCEPTADOR: VOLTAR PARA RESERVAS ═══════
            var comandosVoltar = new[]
            {
                "voltar", "voltando", "retornar",
                "voltar para reserva", "voltar pra reserva",
                "e a reserva", "e minha reserva",
                "sobre a reserva", "minha reserva",
                "quero alterar", "quero cancelar",
                "continuar alteracao", "continuar alteração"
            };

            var querVoltar = comandosVoltar.Any(cmd => textoParaDeteccao.Contains(cmd));

            if (querVoltar)
            {
                var contextoPausado = await _conversationRepository.ObterContextoAsync(idConversa);

                if (contextoPausado != null && contextoPausado.Estado == "pausado_para_outros_assuntos")
                {
                    _logger.LogInformation(
                        "[Conversa={Conversa}] 🔄 COMANDO VOLTAR detectado - restaurando contexto de reservas",
                        idConversa);

                    var estadoAnterior = contextoPausado.DadosColetados?.ContainsKey("estado_anterior") == true
                        ? contextoPausado.DadosColetados["estado_anterior"]?.ToString()
                        : "aguardando_escolha_acao";

                    var contextoRestaurado = new ConversationContext
                    {
                        Estado = estadoAnterior ?? "aguardando_escolha_acao",
                        ReservaIdPendente = contextoPausado.ReservaIdPendente,
                        DadosColetados = contextoPausado.DadosColetados,
                        ReservaSnapshot = contextoPausado.ReservaSnapshot,
                        ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
                    };

                    if (contextoRestaurado.DadosColetados != null)
                    {
                        contextoRestaurado.DadosColetados.Remove("estado_anterior");
                        contextoRestaurado.DadosColetados.Remove("pausado_em");
                    }

                    await _conversationRepository.SalvarContextoAsync(idConversa, contextoRestaurado);

                    string mensagemRetorno;

                    if (contextoPausado.ReservaIdPendente.HasValue && contextoPausado.ReservaIdPendente.Value > 0)
                    {
                        var reserva = await _reservaRepository.BuscarPorIdAsync(contextoPausado.ReservaIdPendente.Value);

                        if (reserva != null)
                        {
                            mensagemRetorno =
                                $"✅ Voltando para sua reserva #{reserva.Codigo}!\n\n" +
                                $"📅 Data: {reserva.DataReserva:dd/MM/yyyy} ({reserva.DataReserva:dddd})\n" +
                                $"⏰ Horário: {reserva.HoraInicio:hh\\:mm}\n" +
                                $"👥 Pessoas: {reserva.QtdPessoas}\n\n" +
                                "O que você gostaria de fazer? 😊";
                        }
                        else
                        {
                            mensagemRetorno = "Pronto! Voltando para suas reservas. Como posso ajudar? 😊";
                        }
                    }
                    else
                    {
                        mensagemRetorno = "Pronto! Voltando para suas reservas. Como posso ajudar? 😊";
                    }

                    await SalvarMensagemRespostaAsync(idConversa, mensagemRetorno);

                    return (true, new AssistantDecision(mensagemRetorno, "none", null, false, null, null));
                }
            }
            // ═══════ FIM INTERCEPTADOR VOLTAR ═══════

            var textoLower = mensagemTexto.ToLower();
            var ehAlteracao = textoLower.Contains("alterar") ||
                               textoLower.Contains("mudar") ||
                               textoLower.Contains("modificar") ||
                               textoLower.Contains("reagendar") ||
                               textoLower.Contains("adicionar") ||
                               textoLower.Contains("atualizar");

            if (ehAlteracao)
            {
                // ? NOVIDADE: Verificar se cliente tem apenas 1 reserva ativa
                var conversa = await _conversationRepository.ObterPorIdAsync(idConversa);
                if (conversa != null)
                {
                    var escopo = await _centralRouting.ResolveEffectiveScopeAsync(idConversa, conversa);
                    if (escopo == null || escopo.IdCliente == Guid.Empty || escopo.IdEstabelecimento == Guid.Empty)
                    {
                        return (false, null);
                    }

                    var reservasAtivas = await ObterReservasAtivasAsync(escopo.IdCliente, escopo.IdEstabelecimento, baseReferencia);

                    // ? Se tem APENAS 1 reserva, não precisa de filtro!
                    if (reservasAtivas.Count == 1)
                    {
                        _logger.LogInformation(
                            "[Conversa={Conversa}] Cliente tem apenas 1 reserva - fast-path DIRETO",
                            idConversa);

                        var reserva = reservasAtivas.First();
                        var reservaId = reserva.Id ?? throw new InvalidOperationException("Reserva carregada sem identificador.");

                        // Tentar extrair dados da mensagem
                        var novoHorario = ExtrairHorario(mensagemTexto);
                        var novaQtd = ExtrairQuantidade(mensagemTexto);

                        // Se conseguiu extrair dados, monta confirmação
                        if (novoHorario != null || novaQtd.HasValue)
                        {
                            var textoMin = mensagemTexto.ToLower();
                            var isDelta = textoMin.Contains("adicionar") || textoMin.Contains("somar") ||
                                         textoMin.Contains("a mais") || textoMin.Contains("a+") || textoMin.Contains("+");

                            var qtdAtual = reserva.QtdPessoas ?? 0;
                            var qtdDepois = novaQtd.HasValue ? (isDelta ? Math.Max(0, qtdAtual + novaQtd.Value) : novaQtd.Value) : qtdAtual;
                            var horaAtual = reserva.HoraInicio.ToString(@"hh\:mm");
                            var horaDepois = string.IsNullOrWhiteSpace(novoHorario) ? horaAtual : novoHorario!;

                            var reply = BuildMsgConfirmacaoAlteracaoComData(
                                reservaId,
                                reserva.DataReserva,
                                null,  // ? dataDepois (null = mantém data atual)
                                horaAtual,
                                horaDepois,
                                qtdAtual,
                                qtdDepois);

                            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
                            {
                                Estado = "aguardando_confirmacao_alteracao",
                                ReservaIdPendente = reservaId,
                                DadosColetados = new Dictionary<string, object>
                                {
                                    { "reserva_id", reservaId },
                                    { "novo_horario", horaDepois },
                                    { "nova_qtd", qtdDepois }
                                },
                                ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)  // ? Aumentado de 10 para 30 minutos
                            });

                            await SalvarMensagemRespostaAsync(idConversa, reply);
                            return (true, new AssistantDecision(reply, "none", null, false, null, null));
                        }
                        else
                        {
                            // Não conseguiu extrair dados, mostra a reserva e pede os dados
                            // ? CORREÇÃO: Usar NomeCliente da reserva (nome informado no momento da reserva)
                            var nomeReserva = reserva.NomeCliente ?? "Cliente";

                            var msg = new StringBuilder();
                            msg.AppendLine($"📋 Reserva #{reserva.Codigo} - Informações atuais:");
                            msg.AppendLine();
                            msg.AppendLine($"👤 Nome: {nomeReserva}");
                            msg.AppendLine($"📅 Data: {DateFormattingHelper.FormatarDataCurta(reserva.DataReserva)}");
                            msg.AppendLine($"⏰ Horário: {reserva.HoraInicio:hh\\:mm}");
                            msg.AppendLine($"👥 Pessoas: {reserva.QtdPessoas}");
                            msg.AppendLine();
                            msg.AppendLine("O que você quer alterar? 🙂");
                            msg.AppendLine("• Horário (ex: 20h, 19:30)");
                            msg.AppendLine("• Quantidade (ex: 8 pessoas, adicionar 2)");

                            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
                            {
                                Estado = "aguardando_dados_alteracao",
                                ReservaIdPendente = reservaId,
                                DadosColetados = new Dictionary<string, object>
                                {
                                    { "reserva_id", reservaId },
                                    { "data_atual", reserva.DataReserva.ToString("yyyy-MM-dd") },
                                    { "hora_atual", reserva.HoraInicio.ToString(@"hh\:mm") },
                                    { "qtd_atual", reserva.QtdPessoas ?? 0 }
                                },
                                ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
                            });

                            var reply = msg.ToString();
                            await SalvarMensagemRespostaAsync(idConversa, reply);
                            return (true, new AssistantDecision(reply, "none", null, false, null, null));
                        }
                    }

                    // ? Se tem múltiplas reservas E tem filtro, processa direto
                    var temFiltro = MensagemContemFiltro(mensagemTexto);

                    if (temFiltro)
                    {
                        _logger.LogInformation(
                            "[Conversa={Conversa}] Cliente especificou filtro - fast-path direto no interceptor",
                            idConversa);

                        var (ok, dec) = await ProcessarAlteracaoDiretaAsync(idConversa, mensagemTexto, baseReferencia);
                        if (ok)
                        {
                            return (true, dec);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "[Conversa={Conversa}] ProcessarAlteracaoDiretaAsync retornou false - deixando IA processar",
                                idConversa);
                            // Deixa cair no return (false, null) no final do método
                            // NÃO imprime "múltiplas reservas sem filtro" pois É MENTIRA
                        }
                    }
                    else if (reservasAtivas.Count > 1)
                    {
                        _logger.LogInformation(
                            "[Conversa={Conversa}] Alteração com múltiplas reservas sem filtro - IA vai listar primeiro",
                            idConversa);
                    }
                }
            }
            // ------- FIM DETECÇÃO -------

            var contexto = await _conversationRepository.ObterContextoAsync(idConversa);

            if (contexto == null || string.IsNullOrWhiteSpace(contexto.Estado))
            {
                // ? NOVO: Log quando não há contexto
                if (contexto == null)
                {
                    _logger.LogDebug("[Conversa={Conversa}] Nenhum contexto ativo encontrado", idConversa);
                }
                return (false, null);
            }

            // ? NOVO: Log do contexto encontrado
            _logger.LogDebug(
                "[Conversa={Conversa}] Contexto ativo: Estado={Estado}, Expira={Expiracao}",
                idConversa, contexto.Estado, contexto.ExpiracaoEstado);

            // ===== BUG 1 FIX: Verificar expiração com log detalhado =====
            if (contexto.ExpiracaoEstado.HasValue)
            {
                var agora = DateTime.UtcNow;
                var tempoRestante = contexto.ExpiracaoEstado.Value - agora;

                _logger.LogDebug(
                    "[Conversa={Conversa}] Verificação de expiração: Agora={Agora:yyyy-MM-dd HH:mm:ss} UTC, Expira={Expira:yyyy-MM-dd HH:mm:ss} UTC, Restante={Restante}min",
                    idConversa,
                    agora,
                    contexto.ExpiracaoEstado.Value,
                    tempoRestante.TotalMinutes);

                if (contexto.ExpiracaoEstado.Value < agora)
                {
                    _logger.LogInformation(
                        "[Conversa={Conversa}] Contexto expirado (restava {Restante}min), limpando",
                        idConversa,
                        tempoRestante.TotalMinutes);
                    await _conversationRepository.LimparContextoAsync(idConversa);
                    return (false, null);
                }
            }
            // ===== FIM BUG 1 FIX =====

            switch (contexto.Estado)
            {
                case "aguardando_escolha_acao":
                {
                    _logger.LogInformation(
                        "[Conversa={Conversa}] Estado: aguardando escolha de ação (A/B/C)",
                        idConversa);

                    var (interceptado, decisao) = await ProcessarEscolhaAcaoAsync(idConversa, mensagemTexto, contexto);
                    return (interceptado, decisao);
                }

                case "aguardando_escolha_reserva":
                {
                    _logger.LogInformation(
                        "[Conversa={Conversa}] Estado: aguardando escolha de reserva",
                        idConversa);

                    if (contexto.DadosColetados == null ||
                        !contexto.DadosColetados.TryGetValue("reservas_ids", out var reservasIdsObj))
                    {
                        _logger.LogWarning(
                            "[Conversa={Conversa}] DadosColetados não tem lista de IDs - limpando contexto",
                            idConversa);
                        await _conversationRepository.LimparContextoAsync(idConversa);
                        return (false, null);
                    }

                    List<long>? reservasIds = null;

                    switch (reservasIdsObj)
                    {
                        case JsonElement json when json.ValueKind == JsonValueKind.Array:
                            reservasIds = json.EnumerateArray()
                                .Where(e => e.ValueKind == JsonValueKind.Number)
                                .Select(e => e.GetInt64())
                                .ToList();
                            break;
                        case JsonElement json when json.ValueKind == JsonValueKind.String:
                        {
                            var raw = json.GetString();
                            if (!string.IsNullOrWhiteSpace(raw))
                            {
                                reservasIds = JsonSerializer.Deserialize<List<long>>(raw);
                            }
                            break;
                        }
                        case string rawString when !string.IsNullOrWhiteSpace(rawString):
                            reservasIds = JsonSerializer.Deserialize<List<long>>(rawString);
                            break;
                        case List<long> listLong:
                            reservasIds = listLong;
                            break;
                        case IEnumerable<long> enumerableLong:
                            reservasIds = enumerableLong.ToList();
                            break;
                        case IEnumerable<object> enumerableObj:
                            reservasIds = enumerableObj
                                .Select(obj =>
                                {
                                    if (obj is JsonElement elem && elem.ValueKind == JsonValueKind.Number)
                                    {
                                        return elem.GetInt64();
                                    }

                                    return Convert.ToInt64(obj);
                                })
                                .ToList();
                            break;
                    }

                    if (reservasIds == null || reservasIds.Count == 0)
                    {
                        _logger.LogWarning(
                            "[Conversa={Conversa}] reservas_ids vazio - limpando contexto",
                            idConversa);
                        await _conversationRepository.LimparContextoAsync(idConversa);
                        return (false, null);
                    }

                    var reservasDoContexto = new List<Reserva>();
                    foreach (var id in reservasIds)
                    {
                        var reserva = await _reservaRepository.BuscarPorIdAsync(id);
                        if (reserva != null)
                        {
                            reservasDoContexto.Add(reserva);
                        }
                    }

                    if (reservasDoContexto.Count == 0)
                    {
                        _logger.LogWarning(
                            "[Conversa={Conversa}] Nenhuma reserva encontrada para IDs armazenados - limpando contexto",
                            idConversa);
                        await _conversationRepository.LimparContextoAsync(idConversa);
                        return (false, null);
                    }

                    var (encontrou, reservaSelecionada) = await ProcessarEscolhaReservaAsync(
                        idConversa,
                        mensagemTexto,
                        reservasDoContexto);

                    if (!encontrou || reservaSelecionada == null)
                    {
                        return (true, null);
                    }

                    _logger.LogInformation(
                        "[Conversa={Conversa}][Reserva=#{Codigo}] Reserva selecionada - entrando em fluxo de alteração",
                        idConversa,
                        reservaSelecionada.Codigo);

                    await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
                    {
                        Estado = "aguardando_dados_alteracao",
                        ReservaIdPendente = reservaSelecionada.Id,
                        DadosColetados = new Dictionary<string, object>
                        {
                            { "reserva_id", reservaSelecionada.Id },
                            { "codigo", reservaSelecionada.Codigo },
                            { "data_atual", reservaSelecionada.DataReserva.ToString("yyyy-MM-dd") },
                            { "hora_atual", reservaSelecionada.HoraInicio.ToString(@"hh\:mm") },
                            { "qtd_atual", reservaSelecionada.QtdPessoas ?? 0 }
                        },
                        ReservaSnapshot = new Dictionary<string, object>
                        {
                            { "id", reservaSelecionada.Id },
                            { "codigo", reservaSelecionada.Codigo },
                            { "data", reservaSelecionada.DataReserva.ToString("yyyy-MM-dd") },
                            { "hora", reservaSelecionada.HoraInicio.ToString(@"hh\:mm") },
                            { "qtd_pessoas", reservaSelecionada.QtdPessoas ?? 0 },
                            { "cliente_id", reservaSelecionada.IdCliente },
                            { "status", reservaSelecionada.Status.ToString() }
                        },
                        ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
                    });

                    var nomeSelecionada = reservaSelecionada.NomeCliente ?? "Cliente";
                    var msg = new StringBuilder();
                    msg.AppendLine($"📋 Reserva #{reservaSelecionada.Codigo} - informações atuais:");
                    msg.AppendLine();
                    msg.AppendLine($"👤 Nome: {nomeSelecionada}");
                    msg.AppendLine($"📅 Data: {DateFormattingHelper.FormatarDataCurta(reservaSelecionada.DataReserva)}");
                    msg.AppendLine($"⏰ Horário: {reservaSelecionada.HoraInicio.ToString(@"hh\:mm")}");
                    msg.AppendLine($"👥 Pessoas: {reservaSelecionada.QtdPessoas}");
                    msg.AppendLine();
                    msg.AppendLine("O que você quer alterar? 😊");
                    msg.AppendLine("• Horário");
                    msg.AppendLine("• Quantidade de pessoas");
                    msg.AppendLine("• Data");

                    var resposta = msg.ToString();
                    await SalvarMensagemRespostaAsync(idConversa, resposta);

                    return (true, new AssistantDecision(resposta, "none", null, false, null, null));
                }

                case "aguardando_dados_alteracao":
                    return await ProcessarDadosAlteracaoAsync(idConversa, mensagemTexto, contexto);

                case "aguardando_confirmacao_alteracao":
                    return await ProcessarConfirmacaoAlteracaoAsync(idConversa, mensagemTexto, contexto);

                default:
                    _logger.LogWarning("[Conversa={Conversa}] Estado de contexto desconhecido: {Estado}",
                        idConversa, contexto.Estado);
                    return (false, null);
            }
        }
        /// <summary>
        /// Processa a escolha do usuário quando há múltiplas reservas.
        /// Aceita: número (1-3), letra (A-C), código (#1234), ou data (15/10)
        /// </summary>
        private async Task<(bool Encontrou, Reserva? ReservaSelecionada)> ProcessarEscolhaReservaAsync(
            Guid idConversa,
            string mensagemTexto,
            List<Reserva> reservasDisponiveis)
        {
            var textoNorm = mensagemTexto.Trim().ToLower();

            _logger.LogInformation(
                "[ProcessarEscolhaReservaAsync][Conversa={Conversa}] Processando escolha: '{Texto}' (Total reservas: {Total})",
                idConversa, mensagemTexto, reservasDisponiveis.Count);

            // ===== MÉTODO 1: Número direto (1, 2, 3) =====
            var numeroEscolha = ExtrairNumeroEscolha(textoNorm);
            if (numeroEscolha.HasValue && numeroEscolha.Value >= 1 && numeroEscolha.Value <= reservasDisponiveis.Count)
            {
                var reserva = reservasDisponiveis[numeroEscolha.Value - 1];
                _logger.LogInformation(
                    "[ProcessarEscolhaReservaAsync][Conversa={Conversa}] ✅ Escolha por NÚMERO: {Numero} → Reserva #{Codigo}",
                    idConversa, numeroEscolha.Value, reserva.Codigo);
                return (true, reserva);
            }

            // ===== MÉTODO 2: Letra (A, B, C) =====
            var letraEscolha = ExtrairOpcaoLetra(textoNorm);
            if (!string.IsNullOrEmpty(letraEscolha))
            {
                var indice = MapearLetraParaIndice(letraEscolha, reservasDisponiveis.Count);
                if (indice.HasValue)
                {
                    var reserva = reservasDisponiveis[indice.Value];
                    _logger.LogInformation(
                        "[ProcessarEscolhaReservaAsync][Conversa={Conversa}] ✅ Escolha por LETRA: {Letra} → Reserva #{Codigo}",
                        idConversa, letraEscolha, reserva.Codigo);
                    return (true, reserva);
                }
            }

            // ===== MÉTODO 3: Código da reserva (#1234 ou 1234) =====
            var codigoEscolhido = ExtrairCodigoReserva(textoNorm);
            if (!string.IsNullOrEmpty(codigoEscolhido))
            {
                var reserva = reservasDisponiveis.FirstOrDefault(r => r.Codigo == codigoEscolhido);
                if (reserva != null)
                {
                    _logger.LogInformation(
                        "[ProcessarEscolhaReservaAsync][Conversa={Conversa}] ✅ Escolha por CÓDIGO: {Codigo}",
                        idConversa, codigoEscolhido);
                    return (true, reserva);
                }
                else
                {
                    _logger.LogWarning(
                        "[ProcessarEscolhaReservaAsync][Conversa={Conversa}] ❌ Código {Codigo} não encontrado nas reservas disponíveis",
                        idConversa, codigoEscolhido);

                    var msgErro = $"❌ Não encontrei a reserva #{codigoEscolhido}.\n\n" +
                                  BuildMsgListagemReservas(reservasDisponiveis);
                    await SalvarMensagemRespostaAsync(idConversa, msgErro);
                    return (true, null);
                }
            }

            // ===== MÉTODO 4: Data (15/10 ou dd/MM) =====
            var dataEscolhida = ExtrairDataReserva(textoNorm);
            if (dataEscolhida.HasValue)
            {
                var reserva = reservasDisponiveis.FirstOrDefault(r => r.DataReserva.Date == dataEscolhida.Value.Date);
                if (reserva != null)
                {
                    _logger.LogInformation(
                        "[ProcessarEscolhaReservaAsync][Conversa={Conversa}] ✅ Escolha por DATA: {Data:dd/MM} → Reserva #{Codigo}",
                        idConversa, dataEscolhida.Value, reserva.Codigo);
                    return (true, reserva);
                }
                else
                {
                    _logger.LogWarning(
                        "[ProcessarEscolhaReservaAsync][Conversa={Conversa}] ❌ Nenhuma reserva encontrada para data {Data:dd/MM}",
                        idConversa, dataEscolhida.Value);

                    var msgErro = $"❌ Não encontrei reserva para {dataEscolhida.Value:dd/MM}.\n\n" +
                                  BuildMsgListagemReservas(reservasDisponiveis);
                    await SalvarMensagemRespostaAsync(idConversa, msgErro);
                    return (true, null);
                }
            }

            // ===== NENHUM MÉTODO FUNCIONOU =====
            _logger.LogWarning(
                "[ProcessarEscolhaReservaAsync][Conversa={Conversa}] ❌ Não conseguiu interpretar escolha: '{Texto}'",
                idConversa, mensagemTexto);

            var msgAjuda = "❓ Não entendi qual reserva você quer alterar.\n\n" +
                           "Você pode escolher de 3 formas:\n" +
                           "• Número da opção (1, 2, 3)\n" +
                           "• Letra da opção (A, B, C)\n" +
                           "• Código da reserva (#1234)\n" +
                           "• Data (15/10)\n\n" +
                           BuildMsgListagemReservas(reservasDisponiveis);

            await SalvarMensagemRespostaAsync(idConversa, msgAjuda);
            return (true, null);
        }

        private async Task<(bool, AssistantDecision?)> ProcessarEscolhaAcaoAsync(
            Guid idConversa,
            string mensagemTexto,
            ConversationContext contexto)
        {
            var textoNorm = mensagemTexto?.Trim().ToLowerInvariant() ?? string.Empty;

            // 1️⃣ TENTAR LETRA PRIMEIRO (mantém comportamento atual)
            var letra = ExtrairOpcaoLetra(textoNorm);
            
            if (!string.IsNullOrEmpty(letra))
            {
                var letraUpper = letra.ToUpperInvariant();
                
                _logger.LogInformation(
                    "[Conversa={Conversa}] Cliente escolheu opção do menu por LETRA: {Letra}",
                    idConversa, letraUpper);

                return letraUpper switch
                {
                    "A" => await ProcessarOpcaoA_CriarReserva(idConversa),
                    "B" => await ProcessarOpcaoB_CancelarReserva(idConversa, contexto, mensagemTexto),
                    "C" => await ProcessarOpcaoC_AlterarReserva(idConversa, contexto, mensagemTexto),
                    "D" => await ProcessarOpcaoD_OutrosAssuntos(idConversa, contexto),
                    _ => await ReexibirMenuPorOpcaoInvalida(idConversa, contexto, letraUpper)
                };
            }

            // 2️⃣ DETECTAR INTENÇÃO POR PALAVRA-CHAVE
            var intencao = DetectarIntencaoPorPalavra(textoNorm);
            
            if (intencao != null)
            {
                _logger.LogInformation(
                    "[Conversa={Conversa}] Cliente escolheu opção por PALAVRA-CHAVE: {Intencao}",
                    idConversa, intencao);

                return intencao switch
                {
                    "criar" => await ProcessarOpcaoA_CriarReserva(idConversa),
                    "cancelar" => await ProcessarOpcaoB_CancelarReserva(idConversa, contexto, mensagemTexto),
                    "alterar" => await ProcessarOpcaoC_AlterarReserva(idConversa, contexto, mensagemTexto),
                    _ => await ReexibirMenuPorNaoEntendimento(idConversa, contexto)
                };
            }

            // 3️⃣ NÃO ENTENDEU - RE-EXIBIR MENU + ATUALIZAR CONTEXTO
            _logger.LogWarning(
                "[Conversa={Conversa}] Não conseguiu interpretar escolha: '{Texto}' - re-exibindo menu",
                idConversa, mensagemTexto);

            return await ReexibirMenuPorNaoEntendimento(idConversa, contexto);
        }

        /// <summary>
        /// Detecta intenção do cliente por palavras-chave na mensagem
        /// </summary>
        private string? DetectarIntencaoPorPalavra(string textoNormalizado)
        {
            // CRIAR/NOVA
            if (textoNormalizado.Contains("criar") || 
                textoNormalizado.Contains("nova") ||
                textoNormalizado.Contains("fazer") || 
                textoNormalizado.Contains("agendar") ||
                textoNormalizado.Contains("marcar") ||
                textoNormalizado.Contains("reservar"))
            {
                _logger.LogDebug("[DetectarIntencaoPorPalavra] Intenção detectada: CRIAR");
                return "criar";
            }

            // CANCELAR
            if (textoNormalizado.Contains("cancelar") || 
                textoNormalizado.Contains("desmarcar") ||
                textoNormalizado.Contains("apagar") || 
                textoNormalizado.Contains("remover") ||
                textoNormalizado.Contains("excluir"))
            {
                _logger.LogDebug("[DetectarIntencaoPorPalavra] Intenção detectada: CANCELAR");
                return "cancelar";
            }

            // ALTERAR
            if (textoNormalizado.Contains("alterar") || 
                textoNormalizado.Contains("mudar") ||
                textoNormalizado.Contains("modificar") || 
                textoNormalizado.Contains("atualizar") ||
                textoNormalizado.Contains("trocar") ||
                textoNormalizado.Contains("reagendar") ||
                textoNormalizado.Contains("ajustar"))
            {
                _logger.LogDebug("[DetectarIntencaoPorPalavra] Intenção detectada: ALTERAR");
                return "alterar";
            }

            return null;
        }

        /// <summary>
        /// Re-exibe o menu quando não entende a resposta + atualiza contexto
        /// </summary>
        private async Task<(bool, AssistantDecision?)> ReexibirMenuPorNaoEntendimento(
            Guid idConversa,
            ConversationContext contexto)
        {
            _logger.LogInformation(
                "[Conversa={Conversa}] Re-exibindo menu por não entendimento",
                idConversa);

            // ✅ ATUALIZAR CONTEXTO (renovar expiração)
            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = contexto.Estado, // Mantém estado atual
                ReservaIdPendente = contexto.ReservaIdPendente,
                DadosColetados = contexto.DadosColetados,
                ReservaSnapshot = contexto.ReservaSnapshot,
                ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30) // ✅ Renova expiração
            });

            var temReservaUnica = contexto.ReservaIdPendente.HasValue && contexto.ReservaIdPendente.Value > 0;
            var temMultiplas = ExtrairFlagBooleana(contexto.DadosColetados, "tem_multiplas_reservas");

            // Montar mensagem de re-exibição
            var msg = new StringBuilder();
            msg.AppendLine("❓ Não entendi sua resposta.");
            msg.AppendLine();
            msg.AppendLine("Por favor, escolha uma das opções:");
            msg.AppendLine();
            msg.AppendLine("A) 🆕 Criar nova reserva");
            
            if (temReservaUnica && !temMultiplas)
            {
                msg.AppendLine("B) ❌ Cancelar sua reserva");
                msg.AppendLine("C) ✏️ Alterar sua reserva");
            }
            else
            {
                msg.AppendLine("B) ❌ Cancelar uma reserva");
                msg.AppendLine("C) ✏️ Alterar uma reserva");
            }
            
            msg.AppendLine();
            msg.AppendLine("Responda com a letra (A, B ou C) ou use palavras como:");
            msg.AppendLine("• \"criar nova\"");
            msg.AppendLine("• \"cancelar\"");
            msg.AppendLine("• \"alterar\"");

            var resposta = msg.ToString();
            await SalvarMensagemRespostaAsync(idConversa, resposta);

            return (true, new AssistantDecision(resposta, "none", null, false, null, null));
        }

        /// <summary>
        /// Re-exibe menu quando letra está fora do range válido
        /// </summary>
        private async Task<(bool, AssistantDecision?)> ReexibirMenuPorOpcaoInvalida(
            Guid idConversa,
            ConversationContext contexto,
            string letraInvalida)
        {
            _logger.LogWarning(
                "[Conversa={Conversa}] Letra inválida recebida: {Letra} - re-exibindo menu",
                idConversa, letraInvalida);

            // ✅ ATUALIZAR CONTEXTO
            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = contexto.Estado,
                ReservaIdPendente = contexto.ReservaIdPendente,
                DadosColetados = contexto.DadosColetados,
                ReservaSnapshot = contexto.ReservaSnapshot,
                ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
            });

            var msg = new StringBuilder();
            msg.AppendLine($"❌ A opção '{letraInvalida}' não é válida.");
            msg.AppendLine();
            msg.AppendLine("Por favor, escolha uma destas opções:");
            msg.AppendLine();
            msg.AppendLine("A) 🆕 Criar nova reserva");
            msg.AppendLine("B) ❌ Cancelar uma reserva");
            msg.AppendLine("C) ✏️ Alterar uma reserva");
            msg.AppendLine();
            msg.AppendLine("Responda com A, B ou C 📝");

            var resposta = msg.ToString();
            await SalvarMensagemRespostaAsync(idConversa, resposta);

            return (true, new AssistantDecision(resposta, "none", null, false, null, null));
        }

        private async Task<(bool, AssistantDecision?)> ProcessarOpcaoA_CriarReserva(Guid idConversa)
        {
            _logger.LogInformation(
                "[Conversa={Conversa}] Opção A selecionada - iniciar fluxo de nova reserva",
                idConversa);

            await _conversationRepository.LimparContextoAsync(idConversa);

            var mensagem =
                "Perfeito! Vamos criar uma nova reserva 🆕\n\n" +
                "Qual seu nome completo?";

            await SalvarMensagemRespostaAsync(idConversa, mensagem);

            return (true, new AssistantDecision(mensagem, "none", null, false, null, null));
        }

        private async Task<(bool, AssistantDecision?)> ProcessarOpcaoB_CancelarReserva(
            Guid idConversa,
            ConversationContext contexto,
            string mensagemOriginal)
        {
            _logger.LogInformation(
                "[Conversa={Conversa}] Opção B selecionada - iniciar fluxo de cancelamento",
                idConversa);

            // ✅ NOVO: Verificar se tem código direto na mensagem
            var codigoDireto = ExtrairCodigoReserva(mensagemOriginal);
            
            if (!string.IsNullOrEmpty(codigoDireto))
            {
                _logger.LogInformation(
                    "[Conversa={Conversa}] Código direto detectado no cancelamento: {Codigo}",
                    idConversa, codigoDireto);

                // Processar cancelamento direto via tool
                try
                {
                    var toolArgs = new
                    {
                        idConversa = idConversa.ToString(),
                        codigoReserva = long.Parse(codigoDireto)
                    };

                    var argsJson = JsonSerializer.Serialize(toolArgs);
                    var toolResult = await _toolExecutor.ExecuteToolAsync("cancelar_reserva", argsJson);

                    // ✅ ATUALIZAR CONTEXTO: Limpar
                    await _conversationRepository.LimparContextoAsync(idConversa);

                    return (true, new AssistantDecision(toolResult, "none", null, false, null, null));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Conversa={Conversa}] Erro ao cancelar via código direto", idConversa);
                    // Continua fluxo normal se falhar
                }
            }

            // ✅ ATUALIZAR CONTEXTO antes de processar
            var temReservaUnica = contexto.ReservaIdPendente.HasValue && contexto.ReservaIdPendente.Value > 0;
            var temMultiplas = ExtrairFlagBooleana(contexto.DadosColetados, "tem_multiplas_reservas");

            if (temReservaUnica && !temMultiplas)
            {
                await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
                {
                    Estado = "aguardando_confirmacao_cancelamento",
                    ReservaIdPendente = contexto.ReservaIdPendente,
                    DadosColetados = contexto.DadosColetados,
                    ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
                });

                var mensagem =
                    "Entendido! Você quer cancelar sua reserva.\n\n" +
                    "Confirma o cancelamento? (sim/não)";

                await SalvarMensagemRespostaAsync(idConversa, mensagem);
                return (true, new AssistantDecision(mensagem, "none", null, false, null, null));
            }

            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = "aguardando_codigo_cancelamento",
                DadosColetados = contexto.DadosColetados,
                ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
            });

            var mensagemSolicitacaoCodigo =
                "Entendido! Qual reserva você quer cancelar?\n\n" +
                "Informe o código da reserva (ex: #1234 ou 1234)";

            await SalvarMensagemRespostaAsync(idConversa, mensagemSolicitacaoCodigo);
            return (true, new AssistantDecision(mensagemSolicitacaoCodigo, "none", null, false, null, null));
        }

        private async Task<(bool, AssistantDecision?)> ProcessarOpcaoC_AlterarReserva(
            Guid idConversa,
            ConversationContext contexto,
            string mensagemOriginal)
        {
            _logger.LogInformation(
                "[Conversa={Conversa}] Opção C selecionada - iniciar fluxo de alteração",
                idConversa);

            // ✅ NOVO: Se tem filtro, usar fast-path direto
            var temFiltro = MensagemContemFiltro(mensagemOriginal);
            
            if (temFiltro)
            {
                _logger.LogInformation(
                    "[Conversa={Conversa}] Filtro detectado na alteração - usando ProcessarAlteracaoDiretaAsync",
                    idConversa);

                var baseReferencia = TimeZoneHelper.GetSaoPauloNow();
                var (sucesso, decisao) = await ProcessarAlteracaoDiretaAsync(
                    idConversa, 
                    mensagemOriginal, 
                    baseReferencia);

                if (sucesso && decisao != null)
                {
                    return (true, decisao);
                }

                _logger.LogWarning(
                    "[Conversa={Conversa}] ProcessarAlteracaoDiretaAsync falhou - continuando fluxo normal",
                    idConversa);
            }

            // ✅ ATUALIZAR CONTEXTO antes de processar
            var temReservaUnica = contexto.ReservaIdPendente.HasValue && contexto.ReservaIdPendente.Value > 0;
            var temMultiplas = ExtrairFlagBooleana(contexto.DadosColetados, "tem_multiplas_reservas");

            if (temReservaUnica && !temMultiplas)
            {
                await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
                {
                    Estado = "aguardando_dados_alteracao",
                    ReservaIdPendente = contexto.ReservaIdPendente,
                    DadosColetados = contexto.DadosColetados,
                    ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
                });

                var mensagem =
                    "Perfeito! Vamos alterar sua reserva ✏️\n\n" +
                    "O que você quer mudar?\n" +
                    "• Data\n" +
                    "• Horário\n" +
                    "• Quantidade de pessoas";

                await SalvarMensagemRespostaAsync(idConversa, mensagem);
                return (true, new AssistantDecision(mensagem, "none", null, false, null, null));
            }

            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = "aguardando_codigo_alteracao",
                DadosColetados = contexto.DadosColetados,
                ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
            });

            var mensagemSolicitacaoCodigo =
                "Entendido! Qual reserva você quer alterar?\n\n" +
                "Informe o código da reserva (ex: #1234 ou 1234)";

            await SalvarMensagemRespostaAsync(idConversa, mensagemSolicitacaoCodigo);
            return (true, new AssistantDecision(mensagemSolicitacaoCodigo, "none", null, false, null, null));
        }

        private async Task<(bool, AssistantDecision?)> ProcessarOpcaoD_OutrosAssuntos(
            Guid idConversa,
            ConversationContext contexto)
        {
            _logger.LogInformation(
                "[Conversa={Conversa}] 💬 Opção D selecionada - pausando contexto para outros assuntos",
                idConversa);

            var contextoPreservado = new ConversationContext
            {
                Estado = "pausado_para_outros_assuntos",
                ReservaIdPendente = contexto.ReservaIdPendente,
                DadosColetados = contexto.DadosColetados ?? new Dictionary<string, object>(),
                ReservaSnapshot = contexto.ReservaSnapshot,
                ExpiracaoEstado = DateTime.UtcNow.AddHours(2)
            };

            contextoPreservado.DadosColetados["estado_anterior"] = contexto.Estado ?? "desconhecido";
            contextoPreservado.DadosColetados["pausado_em"] = DateTime.UtcNow.ToString("o");

            await _conversationRepository.SalvarContextoAsync(idConversa, contextoPreservado);

            _logger.LogInformation(
                "[Conversa={Conversa}] ✅ Contexto pausado preservando: ReservaId={ReservaId}, EstadoAnterior={EstadoAnterior}",
                idConversa,
                contexto.ReservaIdPendente,
                contexto.Estado);

            var mensagem =
                "Perfeito! Fique à vontade para perguntar 😊\n\n" +
                "Posso te ajudar com:\n\n" +
                "🍽️ Cardápio e pratos especiais\n" +
                "📍 Endereço e localização\n" +
                "🕐 Horários de funcionamento\n" +
                "💳 Formas de pagamento\n" +
                "🎉 Eventos e promoções\n" +
                "📞 Contato e redes sociais\n" +
                "🚗 Estacionamento\n\n" +
                "💡 Quando quiser voltar para suas reservas, é só me avisar! 😊";

            await SalvarMensagemRespostaAsync(idConversa, mensagem);

            return (true, new AssistantDecision(mensagem, "none", null, false, null, null));
        }

        private static bool ExtrairFlagBooleana(IDictionary<string, object>? dados, string chave)
        {
            if (dados == null) return false;
            if (!dados.TryGetValue(chave, out var valor)) return false;

            switch (valor)
            {
                case bool b:
                    return b;
                case JsonElement json:
                    return json.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.String when bool.TryParse(json.GetString(), out var boolValue) => boolValue,
                        JsonValueKind.Number => json.TryGetInt32(out var numero) && numero != 0,
                        _ => false
                    };
                default:
                    if (valor is string str && bool.TryParse(str, out var strBool))
                        return strBool;

                    if (valor is IConvertible convertible)
                    {
                        try
                        {
                            return Convert.ToInt32(convertible) != 0;
                        }
                        catch
                        {
                            return false;
                        }
                    }

                    return false;
            }
        }

        private async Task<(bool, AssistantDecision?)> ProcessarDadosAlteracaoAsync(
            Guid idConversa,
            string mensagemTexto,
            ConversationContext contexto)
        {
            // ✅ baseReferencia deve ser obtido aqui
            var baseReferencia = TimeZoneHelper.GetSaoPauloNow();

            _logger.LogInformation(
                "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}] INÍCIO - Mensagem: '{Mensagem}'",
                idConversa, mensagemTexto);

            var idReserva = contexto.ReservaIdPendente ?? 0;
            if (idReserva == 0)
            {
                _logger.LogWarning("[Conversa={Conversa}] ReservaIdPendente é zero", idConversa);
                await _conversationRepository.LimparContextoAsync(idConversa);
                return (false, null);
            }

            var reserva = await _reservaRepository.BuscarPorIdAsync(idReserva);
            if (reserva == null)
            {
                _logger.LogWarning("[Conversa={Conversa}] Reserva {IdReserva} não encontrada", idConversa, idReserva);
                await _conversationRepository.LimparContextoAsync(idConversa);
                return (false, null);
            }

            var codigoReserva = reserva.Codigo;

            // Recuperar dados já coletados
            var dadosContexto = contexto.DadosColetados ?? new Dictionary<string, object>();

            _logger.LogDebug(
                "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] Dados contexto atuais: {Dados}",
                idConversa, codigoReserva,
                string.Join(", ", dadosContexto.Keys));

            // Extrair novos dados da mensagem
            var novoHorario = ExtrairHorario(mensagemTexto);
            var novaQtd = ExtrairQuantidade(mensagemTexto);
            var novaData = ExtrairDataPreferencial(mensagemTexto, baseReferencia, reserva.DataReserva);

            _logger.LogInformation(
                "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] Extração: Horario={Horario}, Qtd={Qtd}, Data={Data}",
                idConversa, codigoReserva,
                novoHorario ?? "null",
                novaQtd?.ToString() ?? "null",
                novaData?.ToString("yyyy-MM-dd") ?? "null");

            // Atualizar dados coletados
            bool houveMudanca = false;

            if (novoHorario != null)
            {
                dadosContexto["novo_horario"] = novoHorario;
                houveMudanca = true;
                _logger.LogDebug("[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] Salvou novo_horario: {Horario}", idConversa, codigoReserva, novoHorario);
            }

            if (novaQtd.HasValue)
            {
                // Verificar se é delta ou absoluto
                var textoMin = mensagemTexto.ToLower();
                var isDelta = textoMin.Contains("adicionar") || textoMin.Contains("somar") ||
                             textoMin.Contains("a mais") || textoMin.Contains("a+") || textoMin.Contains("+");

                var qtdAtual = reserva.QtdPessoas ?? 0;
                var qtdFinal = isDelta ? Math.Max(0, qtdAtual + novaQtd.Value) : novaQtd.Value;

                dadosContexto["nova_qtd"] = qtdFinal;
                houveMudanca = true;

                _logger.LogDebug(
                    "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] Salvou nova_qtd: {Qtd} (isDelta={IsDelta})",
                    idConversa, codigoReserva, qtdFinal, isDelta);
            }

            if (novaData.HasValue)
            {
                dadosContexto["nova_data"] = novaData.Value.ToString("yyyy-MM-dd");
                dadosContexto["data_especificada"] = true;
                houveMudanca = true;

                _logger.LogInformation(
                    "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] ✅ Salvou nova_data: {Data:yyyy-MM-dd}",
                    idConversa, codigoReserva, novaData.Value);
            }

            // Verificar se cliente disse que quer mudar algo mas não especificou
            var textoLower = mensagemTexto.ToLower();
            var querMudarHorario = (textoLower.Contains("horário") || textoLower.Contains("horario") ||
                                    textoLower.Contains("hora")) && novoHorario == null;
            var querMudarQtd = (textoLower.Contains("pessoa") || textoLower.Contains("gente") ||
                                textoLower.Contains("quantidade")) && !novaQtd.HasValue;
            var querMudarData = (textoLower.Contains("data") || textoLower.Contains("dia")) &&
                                !novaData.HasValue && !dadosContexto.ContainsKey("data_especificada");

            _logger.LogDebug(
                "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] Flags: querMudarHorario={Horario}, querMudarQtd={Qtd}, querMudarData={Data}",
                idConversa, codigoReserva, querMudarHorario, querMudarQtd, querMudarData);

            // Se disse que quer mudar mas não especificou, perguntar
            if (querMudarHorario)
            {
                _logger.LogInformation(
                    "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] Cliente quer mudar HORÁRIO mas não especificou",
                    idConversa, codigoReserva);

                var msg = $"⏰ Horário atual: {reserva.HoraInicio:hh\\:mm}\n\n" +
                          $"Qual o novo horário? 😊\n" +
                          $"(Ex: 20h, 19:30, 21h30)";

                await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
                {
                    Estado = "aguardando_dados_alteracao",
                    ReservaIdPendente = idReserva,
                    DadosColetados = dadosContexto,
                    ReservaSnapshot = contexto.ReservaSnapshot,
                    ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
                });

                await SalvarMensagemRespostaAsync(idConversa, msg);
                return (true, new AssistantDecision(msg, "none", null, false, null, null));
            }

            if (querMudarQtd)
            {
                _logger.LogInformation(
                    "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] Cliente quer mudar QUANTIDADE mas não especificou",
                    idConversa, codigoReserva);

                var msg = $"👥 Quantidade atual: {reserva.QtdPessoas} pessoas\n\n" +
                          $"Quantas pessoas agora? 😊\n" +
                          $"(Ex: 8 pessoas, adicionar 2)";

                await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
                {
                    Estado = "aguardando_dados_alteracao",
                    ReservaIdPendente = idReserva,
                    DadosColetados = dadosContexto,
                    ReservaSnapshot = contexto.ReservaSnapshot,
                    ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
                });

                await SalvarMensagemRespostaAsync(idConversa, msg);
                return (true, new AssistantDecision(msg, "none", null, false, null, null));
            }

            if (querMudarData)
            {
                _logger.LogInformation(
                    "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] Cliente quer mudar DATA mas não especificou - perguntando",
                    idConversa, codigoReserva);

                var msg = new StringBuilder();
                msg.AppendLine($"📅 Data atual da reserva #{reserva.Codigo}:");
                msg.AppendLine(DateFormattingHelper.FormatarDataCurta(reserva.DataReserva));
                msg.AppendLine();
                msg.AppendLine("Qual a nova data que você prefere? 🙂");
                msg.AppendLine("(Ex: dia 15, 20/10, sexta-feira)");

                await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
                {
                    Estado = "aguardando_dados_alteracao",
                    ReservaIdPendente = idReserva,
                    DadosColetados = dadosContexto,
                    ReservaSnapshot = contexto.ReservaSnapshot,
                    ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
                });

                var reply = msg.ToString();
                await SalvarMensagemRespostaAsync(idConversa, reply);

                _logger.LogDebug(
                    "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] Perguntando nova data ao usuário",
                    idConversa, codigoReserva);

                return (true, new AssistantDecision(reply, "none", null, false, null, null));
            }

            // Se não houve mudança E não está pedindo algo específico, não intercepta
            if (!houveMudanca)
            {
                _logger.LogInformation(
                    "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] Nenhuma mudança detectada - deixando IA processar",
                    idConversa, codigoReserva);
                return (false, null);
            }

            // Construir resumo das alterações
            _logger.LogInformation(
                "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] Mudanças coletadas - montando confirmação",
                idConversa, codigoReserva);

            var dataAtual = reserva.DataReserva;
            var horaAtual = reserva.HoraInicio.ToString(@"hh\:mm");
            var qtdAtualFinal = reserva.QtdPessoas ?? 0;

            DateTime? dataFinal = null;
            if (dadosContexto.TryGetValue("nova_data", out var dataObj))
            {
                if (DateTime.TryParse(dataObj.ToString(), out var dataParsed))
                {
                    dataFinal = dataParsed;
                    _logger.LogDebug("[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] Data final: {Data:yyyy-MM-dd}", idConversa, codigoReserva, dataFinal);
                }
            }

            var horaFinal = dadosContexto.TryGetValue("novo_horario", out var horaObj)
                ? horaObj.ToString()
                : horaAtual;

            var qtdFinalValue = dadosContexto.TryGetValue("nova_qtd", out var qtdObj)
                ? Convert.ToInt32(qtdObj)
                : qtdAtualFinal;

            _logger.LogInformation(
                "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] Valores finais - Data: {Data}, Hora: {Hora}, Qtd: {Qtd}",
                idConversa, codigoReserva,
                dataFinal?.ToString("yyyy-MM-dd") ?? "mantém",
                horaFinal,
                qtdFinalValue);

            var resumo = BuildMsgConfirmacaoAlteracaoComData(
                idReserva,
                dataAtual,
                dataFinal,
                horaAtual,
                horaFinal!,
                qtdAtualFinal,
                qtdFinalValue);

            // Salvar contexto com estado de confirmação
            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = "aguardando_confirmacao_alteracao",
                ReservaIdPendente = idReserva,
                DadosColetados = dadosContexto,
                ReservaSnapshot = contexto.ReservaSnapshot,
                ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
            });

            await SalvarMensagemRespostaAsync(idConversa, resumo);

            _logger.LogInformation(
                "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] ✅ FIM - Aguardando confirmação",
                idConversa, codigoReserva);

            return (true, new AssistantDecision(resumo, "none", null, false, null, null));
        }

        private async Task<(bool, AssistantDecision?)> ProcessarConfirmacaoAlteracaoAsync(
            Guid idConversa,
            string mensagemTexto,
            ConversationContext contexto)
        {
            var textoNorm = mensagemTexto.Trim().ToLower();

            var codigoReserva = "desconhecido";
            var idReservaContexto = contexto.ReservaIdPendente ?? 0;
            if (idReservaContexto != 0)
            {
                try
                {
                    var reserva = await _reservaRepository.BuscarPorIdAsync(idReservaContexto);
                    if (reserva?.Codigo != null)
                    {
                        codigoReserva = reserva.Codigo;
                    }
                    else
                    {
                        codigoReserva = idReservaContexto.ToString();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ProcessarConfirmacaoAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] Falha ao carregar reserva para confirmação", idConversa);
                    codigoReserva = idReservaContexto.ToString();
                }
            }

            // ? DETECÇÃO ULTRA-COMPLETA DE CONFIRMAÇÕES (100+ variações)
            var confirmacoesExatas = new HashSet<string>
    {
        "sim", "s", "ss", "ok", "okay", "oki", "oky",
        "blz", "beleza", "show", "suave", "massa", "top", "demais", "perfeito",
        "isso", "certeza", "certo", "positivo", "afirmativo",
        "tmj", "vamo", "bora", "dale", "valeu", "fechou", "fexa", "firmeza",
        "tranquilo", "tranks", "de boa", "partiu", "simbora",
        "aham", "uhum", "ahan", "sim sim", "sisim", "simsim",
        "sô", "ô", "opa", "bão", "daora", "dahora",
        "pode crer", "ta valendo", "tá valendo", "manda ver", "manda bala",
        "👍", "👌", "👏", "😄", "😁", "🙂", "🙌"
    };

            var confirmacoesContains = new[]
            {
        "eu confirmo","confirma", "confirmo", "isso mesmo", "isso aí", "isso ai",
        "é isso", "exato", "exatamente", "correto", "certinho",
        "pode sim", "pode ir", "pode mandar", "pode fazer",
        "tudo bem", "tudo certo", "tá bom", "tá ok", "ta bom", "ta ok",
        "está bom", "está ok", "com certeza", "claro", "óbvio", "obvio",
        "lógico", "logico", "autorizo", "aprovado", "aprovo",
        "de acordo", "acordo", "concordo", "sem problema", "👍", "👌", "🙌"
    };

            var ehConfirmacao = confirmacoesExatas.Contains(textoNorm) ||
                                confirmacoesContains.Any(c => textoNorm.Contains(c));

            // ? NOVO: Detectar se é confirmação MAS com mudança adicional
            var temMudancaAdicional = textoNorm.Contains("tbm") ||
                                       textoNorm.Contains("também") ||
                                       textoNorm.Contains("tambem") ||
                                       (textoNorm.Contains(" e ") &&
                                        (textoNorm.Contains("quero") || textoNorm.Contains("mudar") || textoNorm.Contains("alterar")));

            // ? EXECUTAR: Chamar tool diretamente quando confirma
            if (ehConfirmacao)
            {
                _logger.LogInformation(
                    "[Conversa={Conversa}] Confirmação detectada: '{Texto}' - Executando atualização via tool",
                    idConversa, mensagemTexto);

                try
                {
                    // Montar argumentos para a tool
                    var toolArgs = new
                    {
                        idConversa = idConversa.ToString()
                        // A tool vai ler o contexto "aguardando_confirmacao_alteracao" e processar
                    };

                    var argsJson = System.Text.Json.JsonSerializer.Serialize(toolArgs);

                    // Chamar tool diretamente
                    var toolResult = await _toolExecutor.ExecuteToolAsync("atualizar_reserva", argsJson);

                    _logger.LogInformation(
                        "[Conversa={Conversa}] Tool atualizar_reserva executada com sucesso",
                        idConversa);

                    // Retornar resultado da tool como resposta
                    return (true, new AssistantDecision(toolResult, "none", null, false, null, null));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "[Conversa={Conversa}] Erro ao executar tool atualizar_reserva após confirmação",
                        idConversa);

                    // Limpar contexto em caso de erro
                    await _conversationRepository.LimparContextoAsync(idConversa);

                    var erroMsg = "Ops! Tive um problema ao processar a confirmação 😵‍💫\n\nPode tentar novamente?";
                    return (true, new AssistantDecision(erroMsg, "none", null, false, null, null));
                }
            }
            else if (textoNorm.Contains("não") || textoNorm.Contains("nao") || textoNorm == "n")
            {
                await _conversationRepository.LimparContextoAsync(idConversa);
                var reply = "Tudo bem! Sua reserva permanece como estava. Se precisar de algo, estou aqui! 🤗";
                await SalvarMensagemRespostaAsync(idConversa, reply);

                return (true, new AssistantDecision(reply, "none", null, false, null, null));
            }

            // Não conseguiu interpretar confirmação, não intercepta
            return (false, null);
        }

        private int? ExtrairNumeroEscolha(string texto)
        {
            texto = texto.ToLower().Trim();

            // Número direto
            if (int.TryParse(texto, out var numero))
                return numero;

            // Palavras
            var mapa = new Dictionary<string, int>
            {
                { "primeiro", 1 }, { "primeira", 1 }, { "um", 1 }, { "1", 1 },
                { "segundo", 2 }, { "segunda", 2 }, { "dois", 2 }, { "2", 2 },
                { "terceiro", 3 }, { "terceira", 3 }, { "tres", 3 }, { "três", 3 }, { "3", 3 },
                { "quarto", 4 }, { "quarta", 4 }, { "quatro", 4 }, { "4", 4 },
                { "quinto", 5 }, { "quinta", 5 }, { "cinco", 5 }, { "5", 5 }
            };

            foreach (var kvp in mapa)
            {
                if (texto.Contains(kvp.Key))
                    return kvp.Value;
            }

            return null;
        }

        private string? ExtrairHorario(string texto)
        {
            var match = Regex.Match(texto, @"(\d{1,2}):?(\d{2})");
            if (match.Success)
            {
                var hora = match.Groups[1].Value.PadLeft(2, '0');
                var minuto = match.Groups[2].Value;
                return $"{hora}:{minuto}";
            }

            match = Regex.Match(texto, @"(\d{1,2})\s*h");
            if (match.Success)
            {
                var hora = match.Groups[1].Value.PadLeft(2, '0');
                return $"{hora}:00";
            }

            return null;
        }

        private int? ExtrairQuantidade(string texto)
        {
            var match = Regex.Match(texto, @"(\d{1,3})\s*pessoas?", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var qtd))
                return qtd;

            match = Regex.Match(texto, @"para\s*(\d{1,3})", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out qtd))
                return qtd;

            return null;
        }

        private async Task SalvarMensagemRespostaAsync(Guid idConversa, string conteudo)
        {
            // Nota: A mensagem será salva e enviada pelo IAResponseHandler
            // Este método apenas registra que o contexto gerou uma resposta
            _logger.LogInformation("[Conversa={Conversa}] Resposta preparada pelo contexto interceptor", idConversa);
            await Task.CompletedTask;
        }

        private async Task<(bool, AssistantDecision?)> ProcessarAlteracaoDiretaAsync(
            Guid idConversa,
            string mensagemTexto,
            DateTime baseReferencia)
        {
            var novoHorario = ExtrairHorario(mensagemTexto);
            var qtd = ExtrairQuantidade(mensagemTexto);
            var textoMin = mensagemTexto.ToLower();
            var isDelta = textoMin.Contains("adicionar") || textoMin.Contains("somar") ||
                         textoMin.Contains("a mais") || textoMin.Contains("a+") || textoMin.Contains("+");

            var dataPreferida = ExtrairDataPreferencial(mensagemTexto, baseReferencia);
            if (!dataPreferida.HasValue)
            {
                _logger.LogWarning(
                    "[Conversa={Conversa}] ProcessarAlteracaoDiretaAsync: Não conseguiu extrair data de '{Texto}'",
                    idConversa, mensagemTexto);
                return (false, null); // Sem data, deixa a IA processar
            }

            var conversa = await _conversationRepository.ObterPorIdAsync(idConversa);
            if (conversa == null || conversa.IdCliente == Guid.Empty)
            {
                return (false, null);
            }

            var escopo = await _centralRouting.ResolveEffectiveScopeAsync(idConversa, conversa);
            if (escopo == null || escopo.IdCliente == Guid.Empty || escopo.IdEstabelecimento == Guid.Empty)
            {
                return (false, null);
            }

            // Buscar todas as reservas confirmadas futuras do cliente
            var todasReservas = await _reservaRepository.ObterPorClienteEstabelecimentoAsync(
                escopo.IdCliente,
                escopo.IdEstabelecimento);
            var agora = baseReferencia;
            var futuras = todasReservas
                .Where(r => r.Status == APIBack.Model.ReservaStatus.Confirmado &&
                           r.DataReserva.Date.Add(r.HoraInicio) > agora)
                .ToList();

            // Regra: 1 reserva por dia por cliente
            var alvo = futuras.FirstOrDefault(r => r.DataReserva.Date == dataPreferida.Value.Date);
            if (alvo == null)
            {
                var diaNum = ExtrairDiaNumerico(mensagemTexto);
                if (diaNum.HasValue)
                    alvo = futuras.FirstOrDefault(r => r.DataReserva.Day == diaNum.Value);
            }

            if (alvo == null)
            {
                _logger.LogWarning(
                    "[Conversa={Conversa}] ProcessarAlteracaoDiretaAsync: Nenhuma reserva encontrada para data {Data}",
                    idConversa, dataPreferida.Value.ToString("dd/MM/yyyy"));
                return (false, null); // Sem reserva, deixa a IA processar
            }

            var alvoId = alvo.Id ?? throw new InvalidOperationException("Reserva selecionada sem identificador.");

            // ? NOVO: Se não tem mudança especificada, pedir os dados
            if (novoHorario == null && !qtd.HasValue)
            {
                _logger.LogInformation(
                    "[Conversa={Conversa}] Reserva encontrada mas sem mudança especificada - pedindo dados",
                    idConversa);

                // ? CORREÇÃO: Usar NomeCliente da reserva (nome informado no momento da reserva)
                var nomeReserva = alvo.NomeCliente ?? "Cliente";

                var msg = new StringBuilder();
                msg.AppendLine($"📋 Reserva #{alvo.Codigo} encontrada:");
                msg.AppendLine();
                msg.AppendLine($"👤 Nome: {nomeReserva}");
                msg.AppendLine($"📅 Data: {DateFormattingHelper.FormatarDataCurta(alvo.DataReserva)}");
                msg.AppendLine($"⏰ Horário: {alvo.HoraInicio:hh\\:mm}");
                msg.AppendLine($"👥 Pessoas: {alvo.QtdPessoas}");
                msg.AppendLine();
                msg.AppendLine("O que você quer alterar? 🙂");
                msg.AppendLine("• Horário (ex: 20h, 19:30)");
                msg.AppendLine("• Quantidade (ex: 8 pessoas, adicionar 2)");

                await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
                {
                    Estado = "aguardando_dados_alteracao",
                    ReservaIdPendente = alvoId,
                    DadosColetados = new Dictionary<string, object>
                    {
                        { "reserva_id", alvoId },
                        { "data_atual", alvo.DataReserva.ToString("yyyy-MM-dd") },
                        { "hora_atual", alvo.HoraInicio.ToString(@"hh\:mm") },
                        { "qtd_atual", alvo.QtdPessoas ?? 0 }
                    },
                    ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
                });

                var reply = msg.ToString();
                await SalvarMensagemRespostaAsync(idConversa, reply);
                return (true, new AssistantDecision(reply, "none", null, false, null, null));
            }

            var qtdAtual = alvo.QtdPessoas ?? 0;
            var qtdDepois = qtd.HasValue ? (isDelta ? Math.Max(0, qtdAtual + qtd.Value) : qtd.Value) : qtdAtual;
            var horaAtual = alvo.HoraInicio.ToString(@"hh\:mm");
            var horaDepois = string.IsNullOrWhiteSpace(novoHorario) ? horaAtual : novoHorario!;

            var replyConfirmacao = BuildMsgConfirmacaoAlteracaoComData(
                alvoId,
                alvo.DataReserva,
                null,  // ? dataDepois (null = mantém data atual)
                horaAtual,
                horaDepois,
                qtdAtual,
                qtdDepois);

            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = "aguardando_confirmacao_alteracao",
                ReservaIdPendente = alvoId,
                DadosColetados = new Dictionary<string, object>
                {
                    { "reserva_id", alvoId },
                    { "novo_horario", horaDepois },
                    { "nova_qtd", qtdDepois }
                },
                ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)  // ? Aumentado de 10 para 30 minutos
            });

            await SalvarMensagemRespostaAsync(idConversa, replyConfirmacao);
            return (true, new AssistantDecision(replyConfirmacao, "none", null, false, null, null));
        }

        // agora com âncora opcional: se informada, usar como base quando for "dia 12", "dd/MM" ou dia da semana
        private DateTime? ExtrairDataPreferencial(string texto, DateTime baseReferencia, DateTime? ancora = null)
        {
            if (string.IsNullOrWhiteSpace(texto)) return null;

            var referencia = baseReferencia.Date;
            var baseAncora = ancora?.Date;

            var norm = RemoveDiacritics(texto.ToLower()).Replace("-feira", "").Trim();

            // ✅ LOG PARA DEBUG
            _logger.LogDebug(
                "[ExtrairDataPreferencial] Input: '{Texto}' | Normalizado: '{Norm}' | Base: {Base:yyyy-MM-dd} | Ancora: {Ancora}",
                texto, norm, referencia, baseAncora?.ToString("yyyy-MM-dd") ?? "null");

            // 1. TERMOS RELATIVOS (prioridade máxima)
            if (norm == "hoje")
            {
                _logger.LogDebug("[ExtrairDataPreferencial] ✅ HOJE -> {Data:yyyy-MM-dd}", referencia);
                return referencia;
            }

            if (norm.Contains("depois") && norm.Contains("amanha"))
            {
                var depoisAmanha = referencia.AddDays(2);
                _logger.LogDebug("[ExtrairDataPreferencial] ✅ DEPOIS DE AMANHÃ -> {Data:yyyy-MM-dd}", depoisAmanha);
                return depoisAmanha;
            }

            if (norm.Contains("amanha"))
            {
                var amanha = referencia.AddDays(1);
                _logger.LogDebug("[ExtrairDataPreferencial] ✅ AMANHÃ -> {Data:yyyy-MM-dd}", amanha);
                return amanha;
            }

            // 1.5. FORMATO "DIA X" - Prioridade antes de formatos numéricos
            var matchDiaTexto = Regex.Match(norm, @"dia\s*(\d{1,2})");
            if (matchDiaTexto.Success)
            {
                int dia = int.Parse(matchDiaTexto.Groups[1].Value);
                
                var mesAtual = referencia.Month;
                var anoAtual = referencia.Year;
                
                // Se o dia já passou no mês atual, avança para o próximo mês
                if (dia < referencia.Day)
                {
                    _logger.LogDebug(
                        "[ExtrairDataPreferencial] Dia {Dia} < dia atual {DiaAtual}, avançando para próximo mês",
                        dia, referencia.Day);
                    
                    mesAtual++;
                    if (mesAtual > 12)
                    {
                        mesAtual = 1;
                        anoAtual++;
                    }
                }
                
                try
                {
                    var dataCalculada = new DateTime(anoAtual, mesAtual, dia).Date;
                    _logger.LogInformation(
                        "[ExtrairDataPreferencial] ✅ DIA X detectado: {Data:yyyy-MM-dd} (entrada: '{Texto}')",
                        dataCalculada, texto);
                    return dataCalculada;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "[ExtrairDataPreferencial] ❌ Dia inválido: {Dia}/{Mes}/{Ano}",
                        dia, mesAtual, anoAtual);
                    return null;
                }
            }

            // 1.6. NÚMERO ISOLADO (1-31) - Apenas números sozinhos na mensagem
            if (Regex.IsMatch(norm, @"^\s*(\d{1,2})\s*$"))
            {
                if (int.TryParse(norm.Trim(), out int diaNumero) && diaNumero >= 1 && diaNumero <= 31)
                {
                    var mesAtual = referencia.Month;
                    var anoAtual = referencia.Year;
                    
                    // Se o dia já passou no mês atual, avança para o próximo mês
                    if (diaNumero < referencia.Day)
                    {
                        _logger.LogDebug(
                            "[ExtrairDataPreferencial] Dia {Dia} < dia atual {DiaAtual}, avançando para próximo mês",
                            diaNumero, referencia.Day);
                        
                        mesAtual++;
                        if (mesAtual > 12)
                        {
                            mesAtual = 1;
                            anoAtual++;
                        }
                    }
                    
                    try
                    {
                        var dataCalculada = new DateTime(anoAtual, mesAtual, diaNumero).Date;
                        _logger.LogInformation(
                            "[ExtrairDataPreferencial] ✅ NÚMERO ISOLADO como dia: {Data:yyyy-MM-dd} (entrada: '{Texto}')",
                            dataCalculada, texto);
                        return dataCalculada;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "[ExtrairDataPreferencial] ❌ Dia inválido: {Dia}/{Mes}/{Ano}",
                            diaNumero, mesAtual, anoAtual);
                        return null;
                    }
                }
            }

            // 2. FORMATOS ABSOLUTOS (dd/MM/yyyy)
            if (DateTime.TryParseExact(norm, "dd/MM/yyyy",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dataCompleta))
            {
                _logger.LogDebug("[ExtrairDataPreferencial] ✅ dd/MM/yyyy -> {Data:yyyy-MM-dd}", dataCompleta);
                return dataCompleta.Date;
            }

            // 3. FORMATO dd/MM (assume ano da âncora ou da referência)
            if (DateTime.TryParseExact(norm, "dd/MM",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parcial))
            {
                var ano = (baseAncora ?? referencia).Year;
                var tentativa = new DateTime(ano, parcial.Month, parcial.Day).Date;

                // Se a data já passou no ano atual, vai para o próximo ano
                if (tentativa < referencia)
                {
                    tentativa = tentativa.AddYears(1);
                }

                _logger.LogDebug("[ExtrairDataPreferencial] ✅ dd/MM -> {Data:yyyy-MM-dd}", tentativa);
                return tentativa;
            }

            // 4. ✅ CRÍTICO: "DIA X" ou números isolados usando helper
            _logger.LogDebug("[ExtrairDataPreferencial] Tentando extrair dia via DateParsingHelper...");

            if (DateParsingHelper.TryExtractDayNumber(norm, out var diaExtraido))
            {
                _logger.LogDebug("[ExtrairDataPreferencial] Helper retornou dia: {Dia}", diaExtraido);

                var mesAtual = referencia.Month;
                var anoAtual = referencia.Year;

                // Se o dia já passou no mês atual, vai para o próximo mês
                if (diaExtraido < referencia.Day)
                {
                    _logger.LogDebug(
                        "[ExtrairDataPreferencial] Dia {Dia} < dia atual {DiaAtual}, avançando para próximo mês",
                        diaExtraido, referencia.Day);

                    mesAtual++;
                    if (mesAtual > 12)
                    {
                        mesAtual = 1;
                        anoAtual++;
                    }
                }

                try
                {
                    var dataCalculada = new DateTime(anoAtual, mesAtual, diaExtraido).Date;
                    _logger.LogInformation(
                        "[ExtrairDataPreferencial] ✅ DIA DETECTADO via helper: {Data:yyyy-MM-dd} (entrada: '{Texto}')",
                        dataCalculada, texto);
                    return dataCalculada;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "[ExtrairDataPreferencial] ❌ Dia inválido: {Dia}/{Mes}/{Ano}",
                        diaExtraido, mesAtual, anoAtual);
                    return null;
                }
            }
            else
            {
                _logger.LogDebug("[ExtrairDataPreferencial] Helper não conseguiu extrair dia numérico");
            }

            // 5. DIAS DA SEMANA
            var dias = new Dictionary<string, DayOfWeek>
            {
                ["domingo"] = DayOfWeek.Sunday,
                ["segunda"] = DayOfWeek.Monday,
                ["terca"] = DayOfWeek.Tuesday,
                ["quarta"] = DayOfWeek.Wednesday,
                ["quinta"] = DayOfWeek.Thursday,
                ["sexta"] = DayOfWeek.Friday,
                ["sabado"] = DayOfWeek.Saturday
            };

            foreach (var kv in dias)
            {
                if (norm.Contains(kv.Key))
                {
                    var origem = baseAncora ?? referencia;
                    var delta = ((int)kv.Value - (int)origem.DayOfWeek + 7) % 7;
                    if (delta == 0) delta = 7;
                    var resultado = origem.AddDays(delta).Date;

                    _logger.LogDebug(
                        "[ExtrairDataPreferencial] ✅ Dia da semana '{DiaSemana}' -> {Data:yyyy-MM-dd}",
                        kv.Key, resultado);
                    return resultado;
                }
            }

            // 6. FALLBACK: parse livre com cultura PT-BR
            if (DateTime.TryParse(
                    texto,
                    new System.Globalization.CultureInfo("pt-BR"),
                    System.Globalization.DateTimeStyles.None,
                    out var livre))
            {
                _logger.LogDebug("[ExtrairDataPreferencial] ✅ Parse livre PT-BR -> {Data:yyyy-MM-dd}", livre);
                return livre.Date;
            }

            // ❌ NÃO CONSEGUIU PARSEAR
            _logger.LogWarning(
                "[ExtrairDataPreferencial] ❌ NÃO CONSEGUIU parsear nenhum formato: '{Texto}' (normalizado: '{Norm}')",
                texto, norm);
            return null;
        }
        private int? ExtrairDiaNumerico(string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return null;
            var m = Regex.Match(texto.ToLower(), @"dia\s*(\d{1,2})");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var dia)) return dia;
            return null;
        }

        private static string RemoveDiacritics(string value)
        {
            var normalized = value.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(ch);
            }
            return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
        }

        private string BuildMsgConfirmacaoAlteracaoComData(
    long codigoReserva,
    DateTime dataAntes,
    DateTime? dataDepois,
    string horaAntes,
    string horaDepois,
    int qtdAntes,
    int qtdDepois)
        {
            // Nota: codigoReserva aqui representa o Id (long). Em fases futuras vamos buscar o Código (string) no banco.
            var codigoExibicao = codigoReserva.ToString();
            var ptbr = new System.Globalization.CultureInfo("pt-BR");
            var sb = new StringBuilder();
            sb.AppendLine($"📋 Reserva #{codigoExibicao} - Confirme as alterações:");
            sb.AppendLine();

            sb.AppendLine("📅 DATA:");
            if (dataDepois.HasValue && dataDepois.Value.Date != dataAntes.Date)
            {
                sb.AppendLine($"↩ Antes: {dataAntes:dd/MM/yyyy} ({dataAntes.ToString("dddd", ptbr)})");
                sb.AppendLine($"➡ Depois: {dataDepois.Value:dd/MM/yyyy} ({dataDepois.Value.ToString("dddd", ptbr)})");
            }
            else
            {
                sb.AppendLine($"✔ Mantém: {dataAntes:dd/MM/yyyy} ({dataAntes.ToString("dddd", ptbr)})");
            }
            sb.AppendLine();

            sb.AppendLine("⏰ HORÁRIO:");
            if (horaDepois == horaAntes)
            {
                sb.AppendLine($"✔ Mantém: {horaAntes}");
            }
            else
            {
                sb.AppendLine($"↩ Antes: {horaAntes}");
                sb.AppendLine($"➡ Depois: {horaDepois}");
            }
            sb.AppendLine();

            sb.AppendLine("👥 PESSOAS:");
            if (qtdDepois == qtdAntes)
            {
                sb.AppendLine($"✔ Mantém: {qtdAntes}");
            }
            else
            {
                sb.AppendLine($"↩ Antes: {qtdAntes}");
                sb.AppendLine($"➡ Depois: {qtdDepois}");
            }
            sb.AppendLine();

            sb.AppendLine("Confirmar essas mudanças? ✅");
            return sb.ToString();
        }

        /// <summary>
        /// Extrai código de reserva (4 dígitos) da mensagem.
        /// Aceita formatos: #1234, 1234, codigo 1234, reserva 1234.
        /// </summary>
        private string? ExtrairCodigoReserva(string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return null;

            var textoNorm = texto.ToLowerInvariant().Trim();

            var match = Regex.Match(textoNorm, "#(\\d{4})\\b");
            if (match.Success)
            {
                var codigo = match.Groups[1].Value;
                _logger.LogDebug("[ExtrairCodigoReserva] Código extraído com #: {Codigo}", codigo);
                return codigo;
            }

            match = Regex.Match(textoNorm, "(?:codigo|código|reserva|resérva)\\s*(\\d{4})\\b");
            if (match.Success)
            {
                var codigo = match.Groups[1].Value;
                _logger.LogDebug("[ExtrairCodigoReserva] Código extraído após palavra-chave: {Codigo}", codigo);
                return codigo;
            }

            match = Regex.Match(textoNorm, "\\b(\\d{4})\\b");
            if (match.Success)
            {
                var codigo = match.Groups[1].Value;
                _logger.LogDebug("[ExtrairCodigoReserva] Código isolado extraído: {Codigo}", codigo);
                return codigo;
            }

            _logger.LogDebug("[ExtrairCodigoReserva] Nenhum código encontrado em: '{Texto}'", texto);
            return null;
        }

        /// <summary>
        /// Extrai letra de escolha (A, B, C...) da mensagem.
        /// </summary>
        private string? ExtrairOpcaoLetra(string textoNormalizado)
        {
            // Normalizar ainda mais: remover acentos
            var texto = textoNormalizado
                .Replace("ã", "a")
                .Replace("á", "a")
                .Replace("ç", "c")
                .Replace("é", "e")
                .Replace("ó", "o")
                .Replace("ú", "u")
                .Replace("í", "i");

            // Padrões que aceitam:
            // - "a", "b", "c", "d" (letra sozinha)
            // - "opcao a", "opção b" (com ou sem acento)
            // - "letra a", "letra b"
            // - "a opcao a", "escolho b"

            var match = Regex.Match(texto, @"\b([abcd])\b", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value.ToUpper();
            }

            return null;
        }
        /// <summary>
        /// Converte letra de opção para índice de lista (A=0, B=1, ...).
        /// </summary>
        private int? MapearLetraParaIndice(string letra, int totalReservas)
        {
            if (string.IsNullOrEmpty(letra) || letra.Length != 1) return null;

            var letraMaiuscula = char.ToUpperInvariant(letra[0]);
            var indice = letraMaiuscula - 'A';
            if (indice < 0 || indice >= totalReservas)
            {
                _logger.LogWarning("[MapearLetraParaIndice] Letra {Letra} fora do range (total: {Total})", letra, totalReservas);
                return null;
            }

            _logger.LogDebug("[MapearLetraParaIndice] Letra {Letra} → Índice {Indice}", letra, indice);
            return indice;
        }

        /// <summary>
        /// Extrai data no formato dd/MM da mensagem.
        /// Se a data já passou neste ano, assume o próximo ano.
        /// </summary>
        private DateTime? ExtrairDataReserva(string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return null;

            var match = Regex.Match(texto, "\\b(\\d{1,2})/(\\d{1,2})\\b");
            if (!match.Success) return null;

            if (!int.TryParse(match.Groups[1].Value, out var dia)) return null;
            if (!int.TryParse(match.Groups[2].Value, out var mes)) return null;

            if (dia < 1 || dia > 31 || mes < 1 || mes > 12)
            {
                _logger.LogWarning("[ExtrairDataReserva] Data inválida: {Dia}/{Mes}", dia, mes);
                return null;
            }

            var anoAtual = DateTime.Now.Year;
            try
            {
                var data = new DateTime(anoAtual, mes, dia);
                if (data.Date < DateTime.Now.Date)
                {
                    data = data.AddYears(1);
                }

                _logger.LogDebug("[ExtrairDataReserva] Data extraída: {Data:yyyy-MM-dd} (texto='{Texto}')", data, texto);
                return data.Date;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ExtrairDataReserva] Erro ao criar data: {Dia}/{Mes}", dia, mes);
                return null;
            }
        }

        private string BuildMsgListagemReservas(List<Reserva> reservas)
        {
            var msg = new StringBuilder();
            msg.AppendLine("📋 Suas reservas ativas:");
            msg.AppendLine();

            char opcao = 'A';
            foreach (var r in reservas)
            {
                msg.AppendLine($"Opção {opcao} - Reserva #{r.Codigo}");
                msg.AppendLine($"📅 {DateFormattingHelper.FormatarDataCurta(r.DataReserva)} às {r.HoraInicio.ToString(@"hh\:mm")}");
                msg.AppendLine($"👥 {r.QtdPessoas} pessoas");
                msg.AppendLine();
                opcao++;
            }

            msg.AppendLine("━━━━━━━━━━━━━━━━━━━━━━");
            msg.AppendLine("Qual você quer alterar?");

            if (reservas.Count > 0)
            {
                msg.AppendLine($"Digite: A ou #{reservas[0].Codigo} ou {reservas[0].DataReserva:dd/MM}");
            }

            msg.AppendLine();
            msg.AppendLine("━━━━━━━━━━━━━━━━━━━━━━");
            msg.AppendLine("Outras opções:");
            msg.AppendLine("1️⃣ Fazer nova reserva");
            msg.AppendLine("2️⃣ Encerrar atendimento");

            return msg.ToString();
        }

        private bool MensagemContemFiltro(string mensagem)
        {
            if (string.IsNullOrWhiteSpace(mensagem))
                return false;

            var textoLower = mensagem.ToLower();

            // Detectar código (#16, "código 16", "reserva 16")
            if (Regex.IsMatch(textoLower,
                @"#\d+|c[oó]digo\s*\d+|reserva\s*\d+"))
            {
                _logger.LogInformation("[ContextInterceptor] Filtro detectado: CÓDIGO");
                return true;
            }

            // Detectar dia específico ("dia 15", "15/10")
            if (Regex.IsMatch(textoLower,
                @"dia\s*\d{1,2}|\d{1,2}/\d{1,2}|\d{1,2}\s+de\s+\w+"))
            {
                _logger.LogInformation("[ContextInterceptor] Filtro detectado: DIA ESPECÍFICO");
                return true;
            }

            // Detectar dia da semana
            var diasSemana = new[] { "domingo", "segunda", "terça", "terca",
                "quarta", "quinta", "sexta", "sábado", "sabado" };
            if (diasSemana.Any(dia => textoLower.Contains(dia)))
            {
                _logger.LogInformation("[ContextInterceptor] Filtro detectado: DIA DA SEMANA");
                return true;
            }

            // Detectar referência temporal
            if (textoLower.Contains("hoje") || textoLower.Contains("amanhã") ||
                textoLower.Contains("amanha"))
            {
                _logger.LogInformation("[ContextInterceptor] Filtro detectado: TEMPORAL");
                return true;
            }

            // Detectar mês
            var meses = new[] { "janeiro", "fevereiro", "março", "marco",
                "abril", "maio", "junho", "julho", "agosto", "setembro",
                "outubro", "novembro", "dezembro" };
            if (meses.Any(mes => textoLower.Contains(mes)))
            {
                _logger.LogInformation("[ContextInterceptor] Filtro detectado: MÊS");
                return true;
            }

            return false;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
