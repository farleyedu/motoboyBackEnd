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

        public ContextInterceptorService(
            IConversationRepository conversationRepository,
            APIBack.Repository.Interface.IReservaRepository reservaRepository,
            IClienteRepository clienteRepository,
            ILogger<ContextInterceptorService> logger,
            ToolExecutorService toolExecutor)
        {
            _conversationRepository = conversationRepository;
            _reservaRepository = reservaRepository;
            _clienteRepository = clienteRepository;
            _logger = logger;
            _toolExecutor = toolExecutor;
        }

        private async Task<List<APIBack.Model.Reserva>> ObterReservasAtivasAsync(Guid idCliente, Guid idEstabelecimento)
        {
            var reservasExistentes = await _reservaRepository.ObterPorClienteEstabelecimentoAsync(idCliente, idEstabelecimento);
            var referenciaAtual = TimeZoneHelper.GetSaoPauloNow();

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

        /// <summary>
        /// Verifica se há contexto ativo e intercepta a mensagem se necessário
        /// </summary>
        /// <returns>True se a mensagem foi interceptada e processada, False se deve seguir para IA</returns>
        public async Task<(bool Intercepted, AssistantDecision? Decision)> TryInterceptAsync(
            Guid idConversa,
            string mensagemTexto)
        {
            // ═══════ DETECÇÃO INTELIGENTE DE FILTROS ═══════
            var textoLower = mensagemTexto.ToLower();
            var ehAlteracao = textoLower.Contains("alterar") ||
                               textoLower.Contains("mudar") ||
                               textoLower.Contains("modificar") ||
                               textoLower.Contains("reagendar") ||
                               textoLower.Contains("adicionar") ||
                               textoLower.Contains("atualizar");

            if (ehAlteracao)
            {
                // ✨ NOVIDADE: Verificar se cliente tem apenas 1 reserva ativa
                var conversa = await _conversationRepository.ObterPorIdAsync(idConversa);
                if (conversa != null)
                {
                    var reservasAtivas = await ObterReservasAtivasAsync(conversa.IdCliente, conversa.IdEstabelecimento);

                    // ✅ Se tem APENAS 1 reserva, não precisa de filtro!
                    if (reservasAtivas.Count == 1)
                    {
                        _logger.LogInformation(
                            "[Conversa={Conversa}] Cliente tem apenas 1 reserva - fast-path DIRETO",
                            idConversa);

                        var reserva = reservasAtivas.First();

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
                                reserva.Id,
                                reserva.DataReserva,
                                null,  // ← dataDepois (null = mantém data atual)
                                horaAtual,
                                horaDepois,
                                qtdAtual,
                                qtdDepois);

                            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
                            {
                                Estado = "aguardando_confirmacao_alteracao",
                                ReservaIdPendente = reserva.Id,
                                DadosColetados = new Dictionary<string, object>
                                {
                                    { "reserva_id", reserva.Id },
                                    { "novo_horario", horaDepois },
                                    { "nova_qtd", qtdDepois }
                                },
                                ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)  // ✨ Aumentado de 10 para 30 minutos
                            });

                            await SalvarMensagemRespostaAsync(idConversa, reply);
                            return (true, new AssistantDecision(reply, "none", null, false, null, null));
                        }
                        else
                        {
                            // Não conseguiu extrair dados, mostra a reserva e pede os dados
                            var cliente = await _clienteRepository.ObterPorIdAsync(reserva.IdCliente);
                            var nomeCliente = cliente?.Nome ?? "Cliente";

                            var msg = new StringBuilder();
                            msg.AppendLine($"📋 Reserva #{reserva.Id} - Informações atuais:");
                            msg.AppendLine();
                            msg.AppendLine($"👤 Nome: {nomeCliente}");
                            msg.AppendLine($"📅 Data: {reserva.DataReserva:dd/MM/yyyy} ({reserva.DataReserva:dddd})");
                            msg.AppendLine($"⏰ Horário: {reserva.HoraInicio:hh\\:mm}");
                            msg.AppendLine($"👥 Pessoas: {reserva.QtdPessoas}");
                            msg.AppendLine();
                            msg.AppendLine("O que você quer alterar? 😊");
                            msg.AppendLine("• Horário (ex: 20h, 19:30)");
                            msg.AppendLine("• Quantidade (ex: 8 pessoas, adicionar 2)");

                            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
                            {
                                Estado = "aguardando_dados_alteracao",
                                ReservaIdPendente = reserva.Id,
                                DadosColetados = new Dictionary<string, object>
                                {
                                    { "reserva_id", reserva.Id },
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

                    // ✅ Se tem múltiplas reservas E tem filtro, processa direto
                    var temFiltro = MensagemContemFiltro(mensagemTexto);

                    if (temFiltro)
                    {
                        _logger.LogInformation(
                            "[Conversa={Conversa}] Cliente especificou filtro - fast-path direto no interceptor",
                            idConversa);

                        var (ok, dec) = await ProcessarAlteracaoDiretaAsync(idConversa, mensagemTexto);
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
            // ═══════ FIM DETECÇÃO ═══════

            var contexto = await _conversationRepository.ObterContextoAsync(idConversa);

            if (contexto == null || string.IsNullOrWhiteSpace(contexto.Estado))
            {
                // ✨ NOVO: Log quando não há contexto
                if (contexto == null)
                {
                    _logger.LogDebug("[Conversa={Conversa}] Nenhum contexto ativo encontrado", idConversa);
                }
                return (false, null);
            }

            // ✨ NOVO: Log do contexto encontrado
            _logger.LogDebug(
                "[Conversa={Conversa}] Contexto ativo: Estado={Estado}, Expira={Expiracao}",
                idConversa, contexto.Estado, contexto.ExpiracaoEstado);

            // Verificar se contexto expirou
            if (contexto.ExpiracaoEstado.HasValue && contexto.ExpiracaoEstado.Value < DateTime.UtcNow)
            {
                _logger.LogInformation("[Conversa={Conversa}] Contexto expirado, limpando", idConversa);
                await _conversationRepository.LimparContextoAsync(idConversa);
                return (false, null);
            }

            switch (contexto.Estado)
            {
                case "aguardando_escolha_reserva":
                    return await ProcessarEscolhaReservaAsync(idConversa, mensagemTexto, contexto);

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

        private async Task<(bool, AssistantDecision?)> ProcessarEscolhaReservaAsync(
            Guid idConversa,
            string mensagemTexto,
            ConversationContext contexto)
        {
            var numeroEscolhido = ExtrairNumeroEscolha(mensagemTexto);

            if (!numeroEscolhido.HasValue)
            {
                // Não conseguiu extrair número, não intercepta (deixa IA processar)
                return (false, null);
            }

            if (contexto.DadosColetados == null ||
                !contexto.DadosColetados.TryGetValue("mapeamento_reservas", out var mapeamentoJson))
            {
                _logger.LogWarning("[Conversa={Conversa}] Mapeamento de reservas não encontrado no contexto", idConversa);
                return (false, null);
            }

            var mapeamento = JsonSerializer.Deserialize<Dictionary<int, long>>(mapeamentoJson.ToString()!);

            if (mapeamento == null || !mapeamento.TryGetValue(numeroEscolhido.Value, out var idReserva))
            {
                var reply = "Não encontrei essa opção. Pode me dizer o número da reserva? (1, 2, 3...) 😊";
                await SalvarMensagemRespostaAsync(idConversa, reply);
                return (true, new AssistantDecision(reply, "none", null, false, null, null));
            }

            // Buscar reserva completa
            var reserva = await _reservaRepository.BuscarPorIdAsync(idReserva);

            if (reserva == null)
            {
                _logger.LogWarning("[Conversa={Conversa}] Reserva {IdReserva} não encontrada", idConversa, idReserva);
                await _conversationRepository.LimparContextoAsync(idConversa);
                return (false, null);
            }

            // Atualizar contexto com reserva escolhida
            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = "aguardando_dados_alteracao",
                ReservaIdPendente = idReserva,
                DadosColetados = new Dictionary<string, object>
                {
                    { "reserva_id", idReserva },
                    { "data_atual", reserva.DataReserva.ToString("yyyy-MM-dd") },
                    { "hora_atual", reserva.HoraInicio.ToString(@"hh\:mm") },
                    { "qtd_atual", reserva.QtdPessoas ?? 0 }
                },
                ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
            });

            // Montar resposta mostrando informações completas
            var cliente = await _clienteRepository.ObterPorIdAsync(reserva.IdCliente);
            var nomeCliente = cliente?.Nome ?? "Cliente";

            var msg = new StringBuilder();
            msg.AppendLine($"📋 Reserva #{reserva.Id} - Informações completas:");
            msg.AppendLine();
            msg.AppendLine($"👤 Nome: {nomeCliente}");
            msg.AppendLine($"📅 Data: {reserva.DataReserva:dd/MM/yyyy} ({reserva.DataReserva:dddd})");
            msg.AppendLine($"⏰ Horário: {reserva.HoraInicio:hh\\:mm}");
            msg.AppendLine($"👥 Pessoas: {reserva.QtdPessoas}");
            msg.AppendLine($"🎫 Código: #{reserva.Id}");
            msg.AppendLine();
            msg.AppendLine("O que você quer alterar? 😊");
            msg.AppendLine("• Horário");
            msg.AppendLine("• Quantidade de pessoas");
            msg.AppendLine("• Data");

            var replyText = msg.ToString();
            await SalvarMensagemRespostaAsync(idConversa, replyText);

            return (true, new AssistantDecision(replyText, "none", null, false, null, null));
        }

        private async Task<(bool, AssistantDecision?)> ProcessarDadosAlteracaoAsync(
            Guid idConversa,
            string mensagemTexto,
            ConversationContext contexto)
        {
            var textoLower = mensagemTexto.ToLower().Trim();

            // ✨ NOVO: Detectar INTENÇÕES (o que o cliente QUER mudar)
            var querMudarHorario = textoLower.Contains("horário") || textoLower.Contains("horario") ||
                                   textoLower.Contains("hora") || textoLower.Contains("mudar hora");

            var querMudarQuantidade = textoLower.Contains("pessoa") || textoLower.Contains("pessoas") ||
                                      textoLower.Contains("quantidade") || textoLower.Contains("qtd") ||
                                      textoLower.Contains("adicionar") || textoLower.Contains("tirar") ||
                                      textoLower.Contains("mudar quantidade");

            var querMudarData = textoLower.Contains("data") || textoLower.Contains("dia") ||
                                textoLower.Contains("mudar data") || textoLower.Contains("trocar dia");

            // Extrair novos dados (horário, quantidade)
            var novoHorario = ExtrairHorario(mensagemTexto);
            var novaQtd = ExtrairQuantidade(mensagemTexto);

            // ✨ NOVO: Detectar se é mudança ADICIONAL (usa "tbm", "também", "e")
            var ehMudancaAdicional = textoLower.Contains("tbm") || textoLower.Contains("também") ||
                                     textoLower.Contains("tambem") ||
                                     (textoLower.Contains(" e ") && (querMudarHorario || querMudarQuantidade));

            // ✨ NOVO: Se é mudança adicional, recuperar mudanças anteriores do contexto
            Dictionary<string, object> mudancasAcumuladas = new();

            if (ehMudancaAdicional && contexto.DadosColetados != null)
            {
                // Recuperar mudanças anteriores
                if (contexto.DadosColetados.TryGetValue("novo_horario", out var horarioAnterior))
                {
                    var horarioStr = horarioAnterior?.ToString();
                    if (!string.IsNullOrWhiteSpace(horarioStr) && horarioStr != "")
                        mudancasAcumuladas["novo_horario"] = horarioStr;
                }

                if (contexto.DadosColetados.TryGetValue("nova_qtd", out var qtdAnterior))
                {
                    var qtdStr = qtdAnterior?.ToString();
                    if (!string.IsNullOrWhiteSpace(qtdStr) && int.TryParse(qtdStr, out var qtdInt) && qtdInt > 0)
                        mudancasAcumuladas["nova_qtd"] = qtdInt;
                }

                _logger.LogInformation(
                    "[Conversa={Conversa}] Mudança ADICIONAL detectada. Recuperando mudanças anteriores: Horário={Horario}, Qtd={Qtd}",
                    idConversa,
                    mudancasAcumuladas.GetValueOrDefault("novo_horario", "nenhum"),
                    mudancasAcumuladas.GetValueOrDefault("nova_qtd", "nenhuma"));
            }

            // ✨ NOVO: Se cliente manifestou intenção MAS não passou valor, perguntar especificamente
            if (novoHorario == null && !novaQtd.HasValue)
            {
                long idReserva = contexto.ReservaIdPendente ?? 0;
                if (idReserva == 0)
                {
                    _logger.LogWarning("[Conversa={Conversa}] ReservaIdPendente não encontrada no contexto", idConversa);
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

                // ✨ NOVO: MANTER mudanças anteriores ao perguntar nova
                var dadosContexto = new Dictionary<string, object>
        {
            { "reserva_id", idReserva },
            { "data_atual", reserva.DataReserva.ToString("yyyy-MM-dd") },
            { "hora_atual", reserva.HoraInicio.ToString(@"hh\:mm") },
            { "qtd_atual", reserva.QtdPessoas ?? 0 }
        };

                // ✨ CRÍTICO: Adicionar mudanças acumuladas ao contexto
                foreach (var mudanca in mudancasAcumuladas)
                {
                    dadosContexto[mudanca.Key] = mudanca.Value;
                    _logger.LogInformation(
                        "[Conversa={Conversa}] Mantendo mudança anterior no contexto: {Key}={Value}",
                        idConversa, mudanca.Key, mudanca.Value);
                }

                // Se cliente quer mudar DATA
                if (querMudarData)
                {
                    _logger.LogInformation(
                        "[Conversa={Conversa}] Cliente quer mudar DATA mas não especificou - perguntando",
                        idConversa);

                    var msg = new StringBuilder();
                    msg.AppendLine($"📅 Data atual da reserva #{idReserva}:");
                    msg.AppendLine($"{reserva.DataReserva:dd/MM/yyyy} ({reserva.DataReserva:dddd})");
                    msg.AppendLine();
                    msg.AppendLine("Qual a nova data que você prefere? 😊");
                    msg.AppendLine("(Ex: dia 15, 20/10, sexta-feira)");

                    // ✨ NOVO: Salvar contexto COM mudanças acumuladas
                    await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
                    {
                        Estado = "aguardando_dados_alteracao",
                        ReservaIdPendente = idReserva,
                        DadosColetados = dadosContexto,
                        ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
                    });

                    var reply = msg.ToString();
                    await SalvarMensagemRespostaAsync(idConversa, reply);
                    return (true, new AssistantDecision(reply, "none", null, false, null, null));
                }

                // Se cliente quer mudar HORÁRIO
                if (querMudarHorario)
                {
                    _logger.LogInformation(
                        "[Conversa={Conversa}] Cliente quer mudar HORÁRIO mas não especificou - perguntando (mudanças anteriores mantidas)",
                        idConversa);

                    var msg = new StringBuilder();
                    msg.AppendLine($"⏰ Horário atual da reserva #{idReserva}:");
                    msg.AppendLine($"{reserva.HoraInicio:hh\\:mm}");
                    msg.AppendLine();
                    msg.AppendLine("Qual o novo horário? 😊");
                    msg.AppendLine("(Ex: 20h, 19:30, 21h30)");

                    // ✨ NOVO: Salvar contexto COM mudanças acumuladas
                    await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
                    {
                        Estado = "aguardando_dados_alteracao",
                        ReservaIdPendente = idReserva,
                        DadosColetados = dadosContexto,
                        ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
                    });

                    var reply = msg.ToString();
                    await SalvarMensagemRespostaAsync(idConversa, reply);
                    return (true, new AssistantDecision(reply, "none", null, false, null, null));
                }

                // Se cliente quer mudar QUANTIDADE
                if (querMudarQuantidade)
                {
                    _logger.LogInformation(
                        "[Conversa={Conversa}] Cliente quer mudar QUANTIDADE mas não especificou - perguntando (mudanças anteriores mantidas)",
                        idConversa);

                    var qtdReserva = reserva.QtdPessoas ?? 0;

                    var msgQtd = new StringBuilder();
                    msgQtd.AppendLine($"👥 Quantidade atual da reserva #{idReserva}:");
                    msgQtd.AppendLine($"{qtdReserva} pessoas");
                    msgQtd.AppendLine();
                    msgQtd.AppendLine("Qual a nova quantidade? 😊");
                    msgQtd.AppendLine("(Ex: 8 pessoas, adicionar 2, tirar 1)");

                    // ✨ NOVO: Salvar contexto COM mudanças acumuladas
                    await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
                    {
                        Estado = "aguardando_dados_alteracao",
                        ReservaIdPendente = idReserva,
                        DadosColetados = dadosContexto,
                        ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
                    });

                    var replyQtd = msgQtd.ToString();
                    await SalvarMensagemRespostaAsync(idConversa, replyQtd);
                    return (true, new AssistantDecision(replyQtd, "none", null, false, null, null));
                }

                // Se não manifestou nenhuma intenção clara, não intercepta (deixa IA processar)
                _logger.LogInformation(
                    "[Conversa={Conversa}] Não conseguiu extrair dados nem detectar intenção clara - deixando IA processar",
                    idConversa);
                return (false, null);
            }

            // ✨ NOVO: Aplicar mudanças acumuladas + novas mudanças
            if (!string.IsNullOrWhiteSpace(novoHorario))
                mudancasAcumuladas["novo_horario"] = novoHorario;

            if (novaQtd.HasValue)
                mudancasAcumuladas["nova_qtd"] = novaQtd.Value;

            long idReservaFinal = contexto.ReservaIdPendente ?? 0;
            if (idReservaFinal == 0)
            {
                _logger.LogWarning("[Conversa={Conversa}] ReservaIdPendente não encontrada no contexto", idConversa);
                await _conversationRepository.LimparContextoAsync(idConversa);
                return (false, null);
            }

            string horaAtual = contexto.DadosColetados?["hora_atual"]?.ToString() ?? "";
            int qtdAtual = int.Parse(contexto.DadosColetados?["qtd_atual"]?.ToString() ?? "0");
            string dataAtual = contexto.DadosColetados?["data_atual"]?.ToString() ?? "";

            var reservaFinal = await _reservaRepository.BuscarPorIdAsync(idReservaFinal);

            if (reservaFinal == null)
            {
                _logger.LogWarning("[Conversa={Conversa}] Reserva {IdReserva} não encontrada", idConversa, idReservaFinal);
                await _conversationRepository.LimparContextoAsync(idConversa);
                return (false, null);
            }

            // ✨ NOVO: Pegar valores finais das mudanças acumuladas
            var horarioFinal = mudancasAcumuladas.GetValueOrDefault("novo_horario")?.ToString() ?? "";
            var qtdFinal = 0;

            if (mudancasAcumuladas.TryGetValue("nova_qtd", out var qtdObj))
            {
                if (qtdObj is int qtdInt)
                    qtdFinal = qtdInt;
                else if (int.TryParse(qtdObj?.ToString(), out var qtdParsed))
                    qtdFinal = qtdParsed;
            }

            // ✨ SIMPLIFICADO: Salvar contexto e deixar IA montar confirmação
            _logger.LogInformation(
                "[Conversa={Conversa}] Dados coletados - salvando contexto e deixando IA processar",
                idConversa);

            // Salvar contexto com dados coletados (a tool vai ler isso e montar confirmação)
            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = "pronto_para_atualizar",
                ReservaIdPendente = idReservaFinal,
                DadosColetados = new Dictionary<string, object>
                {
                    { "reserva_id", idReservaFinal },
                    { "novo_horario", horarioFinal },
                    { "nova_qtd", qtdFinal > 0 ? qtdFinal : qtdAtual }
                },
                ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
            });

            // NÃO intercepta - deixa IA chamar tool atualizar_reserva
            // A tool vai ver o contexto "pronto_para_atualizar" e montar a confirmação
            return (false, null);
        }

        private async Task<(bool, AssistantDecision?)> ProcessarConfirmacaoAlteracaoAsync(
            Guid idConversa,
            string mensagemTexto,
            ConversationContext contexto)
        {
            var textoNorm = mensagemTexto.Trim().ToLower();

            // ✨ DETECÇÃO ULTRA-COMPLETA DE CONFIRMAÇÕES (100+ variações)
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
        "👍", "✅", "✔️", "☑️", "👌", "🤝", "🙌"
    };

            var confirmacoesContains = new[]
            {
        "eu confirmo","confirma", "confirmo", "isso mesmo", "isso aí", "isso ai",
        "é isso", "exato", "exatamente", "correto", "certinho",
        "pode sim", "pode ir", "pode mandar", "pode fazer",
        "tudo bem", "tudo certo", "tá bom", "tá ok", "ta bom", "ta ok",
        "está bom", "está ok", "com certeza", "claro", "óbvio", "obvio",
        "lógico", "logico", "autorizo", "aprovado", "aprovo",
        "de acordo", "acordo", "concordo", "sem problema", "👍", "✅", "👌"
    };

            var ehConfirmacao = confirmacoesExatas.Contains(textoNorm) ||
                                confirmacoesContains.Any(c => textoNorm.Contains(c));

            // ✨ NOVO: Detectar se é confirmação MAS com mudança adicional
            var temMudancaAdicional = textoNorm.Contains("tbm") ||
                                       textoNorm.Contains("também") ||
                                       textoNorm.Contains("tambem") ||
                                       (textoNorm.Contains(" e ") &&
                                        (textoNorm.Contains("quero") || textoNorm.Contains("mudar") || textoNorm.Contains("alterar")));

            // ✅ EXECUTAR: Chamar tool diretamente quando confirma
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

                    var erroMsg = "Ops! Tive um problema ao processar a confirmação 😔\n\nPode tentar novamente?";
                    return (true, new AssistantDecision(erroMsg, "none", null, false, null, null));
                }
            }
            else if (textoNorm.Contains("não") || textoNorm.Contains("nao") || textoNorm == "n")
            {
                await _conversationRepository.LimparContextoAsync(idConversa);
                var reply = "Tudo bem! Sua reserva permanece como estava. Se precisar de algo, estou aqui! 😊";
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
            string mensagemTexto)
        {
            var novoHorario = ExtrairHorario(mensagemTexto);
            var qtd = ExtrairQuantidade(mensagemTexto);
            var textoMin = mensagemTexto.ToLower();
            var isDelta = textoMin.Contains("adicionar") || textoMin.Contains("somar") ||
                         textoMin.Contains("a mais") || textoMin.Contains("a+") || textoMin.Contains("+");

            var dataPreferida = ExtrairDataPreferencial(mensagemTexto);
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

            var idCliente = conversa.IdCliente;
            var idEstabelecimento = conversa.IdEstabelecimento;

            // Buscar todas as reservas confirmadas futuras do cliente
            var todasReservas = await _reservaRepository.ObterPorClienteEstabelecimentoAsync(idCliente, idEstabelecimento);
            var agora = TimeZoneHelper.GetSaoPauloNow();
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

            // ✨ NOVO: Se não tem mudança especificada, pedir os dados
            if (novoHorario == null && !qtd.HasValue)
            {
                _logger.LogInformation(
                    "[Conversa={Conversa}] Reserva encontrada mas sem mudança especificada - pedindo dados",
                    idConversa);

                var cliente = await _clienteRepository.ObterPorIdAsync(alvo.IdCliente);
                var nomeCliente = cliente?.Nome ?? "Cliente";

                var msg = new StringBuilder();
                msg.AppendLine($"📋 Reserva #{alvo.Id} encontrada:");
                msg.AppendLine();
                msg.AppendLine($"👤 Nome: {nomeCliente}");
                msg.AppendLine($"📅 Data: {alvo.DataReserva:dd/MM/yyyy} ({alvo.DataReserva:dddd})");
                msg.AppendLine($"⏰ Horário: {alvo.HoraInicio:hh\\:mm}");
                msg.AppendLine($"👥 Pessoas: {alvo.QtdPessoas}");
                msg.AppendLine();
                msg.AppendLine("O que você quer alterar? 😊");
                msg.AppendLine("• Horário (ex: 20h, 19:30)");
                msg.AppendLine("• Quantidade (ex: 8 pessoas, adicionar 2)");

                await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
                {
                    Estado = "aguardando_dados_alteracao",
                    ReservaIdPendente = alvo.Id,
                    DadosColetados = new Dictionary<string, object>
                    {
                        { "reserva_id", alvo.Id },
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
                alvo.Id,
                alvo.DataReserva,
                null,  // ← dataDepois (null = mantém data atual)
                horaAtual,
                horaDepois,
                qtdAtual,
                qtdDepois);

            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = "aguardando_confirmacao_alteracao",
                ReservaIdPendente = alvo.Id,
                DadosColetados = new Dictionary<string, object>
                {
                    { "reserva_id", alvo.Id },
                    { "novo_horario", horaDepois },
                    { "nova_qtd", qtdDepois }
                },
                ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)  // ✨ Aumentado de 10 para 30 minutos
            });

            await SalvarMensagemRespostaAsync(idConversa, replyConfirmacao);
            return (true, new AssistantDecision(replyConfirmacao, "none", null, false, null, null));
        }

        // agora com âncora opcional: se informada, usar como base quando for "dia 12", "dd/MM" ou dia da semana
        private DateTime? ExtrairDataPreferencial(string texto, DateTime? ancora = null)
        {
            if (string.IsNullOrWhiteSpace(texto)) return null;

            var hoje = TimeZoneHelper.GetSaoPauloNow().Date;
            var referencia = hoje; // default
            var baseAncora = ancora?.Date;

            var norm = RemoveDiacritics(texto.ToLower()).Replace("-feira", "").Trim();

            // relativos
            if (norm.Contains("hoje")) return hoje;
            if (norm.Contains("amanha") || norm.Contains("amanhã")) return hoje.AddDays(1);
            if (norm.Contains("depois de amanha")) return hoje.AddDays(2);

            // dd/MM/yyyy
            if (DateTime.TryParseExact(norm, "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var absoluto))
                return absoluto.Date;

            // dd/MM (usar âncora se existir; senão hoje)
            if (DateTime.TryParseExact(norm, "dd/MM", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parcial))
            {
                var ano = (baseAncora ?? referencia).Year;
                var comp = new DateTime(ano, parcial.Month, parcial.Day);

                // regra da “data mais próxima” da âncora: se <= âncora, vai para próximo ano
                if (baseAncora.HasValue && comp <= baseAncora) comp = comp.AddYears(1);
                else if (!baseAncora.HasValue && comp < referencia) comp = comp.AddYears(1);

                return comp.Date;
            }

            // dia da semana — próxima ocorrência a partir da âncora (se houver), senão hoje
            var dias = new Dictionary<string, DayOfWeek> {
        {"domingo", DayOfWeek.Sunday}, {"segunda", DayOfWeek.Monday},
        {"terca", DayOfWeek.Tuesday}, {"terça", DayOfWeek.Tuesday},
        {"quarta", DayOfWeek.Wednesday}, {"quinta", DayOfWeek.Thursday},
        {"sexta", DayOfWeek.Friday}, {"sabado", DayOfWeek.Saturday}, {"sábado", DayOfWeek.Saturday}
    };
            foreach (var kv in dias)
            {
                if (norm.Contains(kv.Key))
                {
                    var origem = baseAncora ?? referencia;
                    var delta = ((int)kv.Value - (int)origem.DayOfWeek + 7) % 7;
                    if (delta == 0) delta = 7; // próxima ocorrência
                    return origem.AddDays(delta).Date;
                }
            }

            // "dia 12"
            var matchDia = Regex.Match(norm, @"dia\s*(\d{1,2})");
            if (matchDia.Success && int.TryParse(matchDia.Groups[1].Value, out var diaNumero) && diaNumero is >= 1 and <= 31)
            {
                var origem = baseAncora ?? referencia;
                var tentativa = new DateTime(origem.Year, origem.Month, diaNumero);
                if (baseAncora.HasValue)
                {
                    // se igual/antes da âncora, vai para mês seguinte (regra que você pediu)
                    if (tentativa <= baseAncora) tentativa = tentativa.AddMonths(1);
                }
                else
                {
                    if (tentativa < referencia) tentativa = tentativa.AddMonths(1);
                }
                return tentativa.Date;
            }

            // parse livre pt-BR
            if (DateTime.TryParse(texto, new System.Globalization.CultureInfo("pt-BR"),
                System.Globalization.DateTimeStyles.None, out var livre))
                return livre.Date;

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
            var ptbr = new System.Globalization.CultureInfo("pt-BR");
            var sb = new StringBuilder();
            sb.AppendLine($"📋 Reserva #{codigoReserva} - Confirme as alterações:");
            sb.AppendLine();

            sb.AppendLine("📅 DATA:");
            if (dataDepois.HasValue && dataDepois.Value.Date != dataAntes.Date)
            {
                sb.AppendLine($"❌ Antes: {dataAntes:dd/MM/yyyy} ({dataAntes.ToString("dddd", ptbr)})");
                sb.AppendLine($"✅ Depois: {dataDepois.Value:dd/MM/yyyy} ({dataDepois.Value.ToString("dddd", ptbr)})");
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
                sb.AppendLine($"❌ Antes: {horaAntes}");
                sb.AppendLine($"✅ Depois: {horaDepois}");
            }
            sb.AppendLine();

            sb.AppendLine("👥 PESSOAS:");
            if (qtdDepois == qtdAntes)
            {
                sb.AppendLine($"✔ Mantém: {qtdAntes}");
            }
            else
            {
                sb.AppendLine($"❌ Antes: {qtdAntes}");
                sb.AppendLine($"✅ Depois: {qtdDepois}");
            }
            sb.AppendLine();

            sb.AppendLine("Confirmar essas mudanças? 😊");
            return sb.ToString();
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
