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
    /// ServiÃ§o responsÃ¡vel por interceptar mensagens quando hÃ¡ contexto de conversa ativo
    /// (ex: escolha de reserva, alteraÃ§Ã£o de dados, confirmaÃ§Ã£o)
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

        /// <summary>
        /// Verifica se hÃ¡ contexto ativo e intercepta a mensagem se necessÃ¡rio
        /// </summary>
        /// <returns>True se a mensagem foi interceptada e processada, False se deve seguir para IA</returns>
        public async Task<(bool Intercepted, AssistantDecision? Decision)> TryInterceptAsync(
            Guid idConversa,
            string mensagemTexto,
            DateTime? timestampMensagemUtc = null)
        {
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

            // ------- DETECÃ‡ÃƒO INTELIGENTE DE FILTROS -------
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
                    var reservasAtivas = await ObterReservasAtivasAsync(conversa.IdCliente, conversa.IdEstabelecimento, baseReferencia);

                    // ? Se tem APENAS 1 reserva, nÃ£o precisa de filtro!
                    if (reservasAtivas.Count == 1)
                    {
                        _logger.LogInformation(
                            "[Conversa={Conversa}] Cliente tem apenas 1 reserva - fast-path DIRETO",
                            idConversa);

                        var reserva = reservasAtivas.First();

                        // Tentar extrair dados da mensagem
                        var novoHorario = ExtrairHorario(mensagemTexto);
                        var novaQtd = ExtrairQuantidade(mensagemTexto);

                        // Se conseguiu extrair dados, monta confirmaÃ§Ã£o
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
                                null,  // ? dataDepois (null = mantÃ©m data atual)
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
                                ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)  // ? Aumentado de 10 para 30 minutos
                            });

                            await SalvarMensagemRespostaAsync(idConversa, reply);
                            return (true, new AssistantDecision(reply, "none", null, false, null, null));
                        }
                        else
                        {
                            // NÃ£o conseguiu extrair dados, mostra a reserva e pede os dados
                            // ? CORREÃ‡ÃƒO: Usar NomeCliente da reserva (nome informado no momento da reserva)
                            var nomeReserva = reserva.NomeCliente ?? "Cliente";

                            var msg = new StringBuilder();
                            msg.AppendLine($"ðŸ“‹ Reserva #{reserva.Codigo} - InformaÃ§Ãµes atuais:");
                            msg.AppendLine();
                            msg.AppendLine($"ðŸ‘¤ Nome: {nomeReserva}");
                            msg.AppendLine($"ðŸ“… Data: {DateFormattingHelper.FormatarDataCurta(reserva.DataReserva)}");
                            msg.AppendLine($"â° HorÃ¡rio: {reserva.HoraInicio:hh\\:mm}");
                            msg.AppendLine($"ðŸ‘¥ Pessoas: {reserva.QtdPessoas}");
                            msg.AppendLine();
                            msg.AppendLine("O que vocÃª quer alterar? ðŸ™‚");
                            msg.AppendLine("â€¢ HorÃ¡rio (ex: 20h, 19:30)");
                            msg.AppendLine("â€¢ Quantidade (ex: 8 pessoas, adicionar 2)");

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

                    // ? Se tem mÃºltiplas reservas E tem filtro, processa direto
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
                            // Deixa cair no return (false, null) no final do mÃ©todo
                            // NÃƒO imprime "mÃºltiplas reservas sem filtro" pois Ã‰ MENTIRA
                        }
                    }
                    else if (reservasAtivas.Count > 1)
                    {
                        _logger.LogInformation(
                            "[Conversa={Conversa}] AlteraÃ§Ã£o com mÃºltiplas reservas sem filtro - IA vai listar primeiro",
                            idConversa);
                    }
                }
            }
            // ------- FIM DETECÃ‡ÃƒO -------

            var contexto = await _conversationRepository.ObterContextoAsync(idConversa);

            if (contexto == null || string.IsNullOrWhiteSpace(contexto.Estado))
            {
                // ? NOVO: Log quando nÃ£o hÃ¡ contexto
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

            // ===== BUG 1 FIX: Verificar expiraÃ§Ã£o com log detalhado =====
            if (contexto.ExpiracaoEstado.HasValue)
            {
                var agora = DateTime.UtcNow;
                var tempoRestante = contexto.ExpiracaoEstado.Value - agora;

                _logger.LogDebug(
                    "[Conversa={Conversa}] VerificaÃ§Ã£o de expiraÃ§Ã£o: Agora={Agora:yyyy-MM-dd HH:mm:ss} UTC, Expira={Expira:yyyy-MM-dd HH:mm:ss} UTC, Restante={Restante}min",
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
                        "[Conversa={Conversa}] Estado: aguardando escolha de aÃ§Ã£o (A/B/C)",
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
                            "[Conversa={Conversa}] DadosColetados nÃ£o tem lista de IDs - limpando contexto",
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
                        "[Conversa={Conversa}][Reserva=#{Codigo}] Reserva selecionada - entrando em fluxo de alteraÃ§Ã£o",
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
                    msg.AppendLine($"ðŸ“‹ Reserva #{reservaSelecionada.Codigo} - informaÃ§Ãµes atuais:");
                    msg.AppendLine();
                    msg.AppendLine($"ðŸ‘¤ Nome: {nomeSelecionada}");
                    msg.AppendLine($"ðŸ“… Data: {DateFormattingHelper.FormatarDataCurta(reservaSelecionada.DataReserva)}");
                    msg.AppendLine($"â° HorÃ¡rio: {reservaSelecionada.HoraInicio.ToString(@"hh\:mm")}");
                    msg.AppendLine($"ðŸ‘¥ Pessoas: {reservaSelecionada.QtdPessoas}");
                    msg.AppendLine();
                    msg.AppendLine("O que vocÃª quer alterar? ðŸ˜Š");
                    msg.AppendLine("â€¢ HorÃ¡rio");
                    msg.AppendLine("â€¢ Quantidade de pessoas");
                    msg.AppendLine("â€¢ Data");

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
        /// Processa a escolha do usuÃ¡rio quando hÃ¡ mÃºltiplas reservas.
        /// Aceita: nÃºmero (1-3), letra (A-C), cÃ³digo (#1234), ou data (15/10)
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

            // ===== MÃ‰TODO 1: NÃºmero direto (1, 2, 3) =====
            var numeroEscolha = ExtrairNumeroEscolha(textoNorm);
            if (numeroEscolha.HasValue && numeroEscolha.Value >= 1 && numeroEscolha.Value <= reservasDisponiveis.Count)
            {
                var reserva = reservasDisponiveis[numeroEscolha.Value - 1];
                _logger.LogInformation(
                    "[ProcessarEscolhaReservaAsync][Conversa={Conversa}] âœ… Escolha por NÃšMERO: {Numero} â†’ Reserva #{Codigo}",
                    idConversa, numeroEscolha.Value, reserva.Codigo);
                return (true, reserva);
            }

            // ===== MÃ‰TODO 2: Letra (A, B, C) =====
            var letraEscolha = ExtrairOpcaoLetra(textoNorm);
            if (!string.IsNullOrEmpty(letraEscolha))
            {
                var indice = MapearLetraParaIndice(letraEscolha, reservasDisponiveis.Count);
                if (indice.HasValue)
                {
                    var reserva = reservasDisponiveis[indice.Value];
                    _logger.LogInformation(
                        "[ProcessarEscolhaReservaAsync][Conversa={Conversa}] âœ… Escolha por LETRA: {Letra} â†’ Reserva #{Codigo}",
                        idConversa, letraEscolha, reserva.Codigo);
                    return (true, reserva);
                }
            }

            // ===== MÃ‰TODO 3: CÃ³digo da reserva (#1234 ou 1234) =====
            var codigoEscolhido = ExtrairCodigoReserva(textoNorm);
            if (!string.IsNullOrEmpty(codigoEscolhido))
            {
                var reserva = reservasDisponiveis.FirstOrDefault(r => r.Codigo == codigoEscolhido);
                if (reserva != null)
                {
                    _logger.LogInformation(
                        "[ProcessarEscolhaReservaAsync][Conversa={Conversa}] âœ… Escolha por CÃ“DIGO: {Codigo}",
                        idConversa, codigoEscolhido);
                    return (true, reserva);
                }
                else
                {
                    _logger.LogWarning(
                        "[ProcessarEscolhaReservaAsync][Conversa={Conversa}] âŒ CÃ³digo {Codigo} nÃ£o encontrado nas reservas disponÃ­veis",
                        idConversa, codigoEscolhido);

                    var msgErro = $"âŒ NÃ£o encontrei a reserva #{codigoEscolhido}.\n\n" +
                                  BuildMsgListagemReservas(reservasDisponiveis);
                    await SalvarMensagemRespostaAsync(idConversa, msgErro);
                    return (true, null);
                }
            }

            // ===== MÃ‰TODO 4: Data (15/10 ou dd/MM) =====
            var dataEscolhida = ExtrairDataReserva(textoNorm);
            if (dataEscolhida.HasValue)
            {
                var reserva = reservasDisponiveis.FirstOrDefault(r => r.DataReserva.Date == dataEscolhida.Value.Date);
                if (reserva != null)
                {
                    _logger.LogInformation(
                        "[ProcessarEscolhaReservaAsync][Conversa={Conversa}] âœ… Escolha por DATA: {Data:dd/MM} â†’ Reserva #{Codigo}",
                        idConversa, dataEscolhida.Value, reserva.Codigo);
                    return (true, reserva);
                }
                else
                {
                    _logger.LogWarning(
                        "[ProcessarEscolhaReservaAsync][Conversa={Conversa}] âŒ Nenhuma reserva encontrada para data {Data:dd/MM}",
                        idConversa, dataEscolhida.Value);

                    var msgErro = $"âŒ NÃ£o encontrei reserva para {dataEscolhida.Value:dd/MM}.\n\n" +
                                  BuildMsgListagemReservas(reservasDisponiveis);
                    await SalvarMensagemRespostaAsync(idConversa, msgErro);
                    return (true, null);
                }
            }

            // ===== NENHUM MÃ‰TODO FUNCIONOU =====
            _logger.LogWarning(
                "[ProcessarEscolhaReservaAsync][Conversa={Conversa}] âŒ NÃ£o conseguiu interpretar escolha: '{Texto}'",
                idConversa, mensagemTexto);

            var msgAjuda = "â“ NÃ£o entendi qual reserva vocÃª quer alterar.\n\n" +
                           "VocÃª pode escolher de 3 formas:\n" +
                           "â€¢ NÃºmero da opÃ§Ã£o (1, 2, 3)\n" +
                           "â€¢ Letra da opÃ§Ã£o (A, B, C)\n" +
                           "â€¢ CÃ³digo da reserva (#1234)\n" +
                           "â€¢ Data (15/10)\n\n" +
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

            var letra = ExtrairOpcaoLetra(textoNorm);
            if (string.IsNullOrEmpty(letra))
            {
                _logger.LogInformation(
                    "[Conversa={Conversa}] Nenhuma letra válida identificada na resposta: '{Texto}'",
                    idConversa,
                    mensagemTexto);

                return (false, null);
            }

            var letraUpper = letra.ToUpperInvariant();

            _logger.LogInformation(
                "[Conversa={Conversa}] Cliente escolheu opção do menu: {Letra}",
                idConversa,
                letraUpper);

            return letraUpper switch
            {
                "A" => await ProcessarOpcaoA_CriarReserva(idConversa),
                "B" => await ProcessarOpcaoB_CancelarReserva(idConversa, contexto),
                "C" => await ProcessarOpcaoC_AlterarReserva(idConversa, contexto),
                _ => TratarLetraInvalida(idConversa, letraUpper)
            };
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
            ConversationContext contexto)
        {
            _logger.LogInformation(
                "[Conversa={Conversa}] Opção B selecionada - iniciar fluxo de cancelamento",
                idConversa);

            var temReservaUnica = contexto.ReservaIdPendente.HasValue && contexto.ReservaIdPendente.Value > 0;
            var temMultiplas = ExtrairFlagBooleana(contexto.DadosColetados, "tem_multiplas_reservas");

            if (temReservaUnica && !temMultiplas)
            {
                await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
                {
                    Estado = "aguardando_confirmacao_cancelamento",
                    ReservaIdPendente = contexto.ReservaIdPendente,
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
            ConversationContext contexto)
        {
            _logger.LogInformation(
                "[Conversa={Conversa}] Opção C selecionada - iniciar fluxo de alteração",
                idConversa);

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
                ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
            });

            var mensagemSolicitacaoCodigo =
                "Entendido! Qual reserva você quer alterar?\n\n" +
                "Informe o código da reserva (ex: #1234 ou 1234)";

            await SalvarMensagemRespostaAsync(idConversa, mensagemSolicitacaoCodigo);

            return (true, new AssistantDecision(mensagemSolicitacaoCodigo, "none", null, false, null, null));
        }

        private (bool, AssistantDecision?) TratarLetraInvalida(Guid idConversa, string letra)
        {
            _logger.LogWarning(
                "[Conversa={Conversa}] Letra inválida recebida na escolha de ação: {Letra}",
                idConversa,
                letra);

            return (false, null);
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
                        JsonValueKind.String when bool.TryParse(json.GetString(), out var parsed) => parsed,
                        JsonValueKind.Number => json.TryGetInt32(out var numero) && numero != 0,
                        _ => false
                    };
                default:
                    if (valor is string str && bool.TryParse(str, out var parsedStr))
                        return parsedStr;

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
        }        private async Task<(bool, AssistantDecision?)> ProcessarDadosAlteracaoAsync(
            Guid idConversa,
            string mensagemTexto,
            ConversationContext contexto)
        {
            // âœ… baseReferencia deve ser obtido aqui
            var baseReferencia = TimeZoneHelper.GetSaoPauloNow();

            _logger.LogInformation(
                "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}] INÃCIO - Mensagem: '{Mensagem}'",
                idConversa, mensagemTexto);

            var idReserva = contexto.ReservaIdPendente ?? 0;
            if (idReserva == 0)
            {
                _logger.LogWarning("[Conversa={Conversa}] ReservaIdPendente Ã© zero", idConversa);
                await _conversationRepository.LimparContextoAsync(idConversa);
                return (false, null);
            }

            var reserva = await _reservaRepository.BuscarPorIdAsync(idReserva);
            if (reserva == null)
            {
                _logger.LogWarning("[Conversa={Conversa}] Reserva {IdReserva} nÃ£o encontrada", idConversa, idReserva);
                await _conversationRepository.LimparContextoAsync(idConversa);
                return (false, null);
            }

            var codigoReserva = reserva.Codigo;

            // Recuperar dados jÃ¡ coletados
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
                "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] ExtraÃ§Ã£o: Horario={Horario}, Qtd={Qtd}, Data={Data}",
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
                // Verificar se Ã© delta ou absoluto
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
                    "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] âœ… Salvou nova_data: {Data:yyyy-MM-dd}",
                    idConversa, codigoReserva, novaData.Value);
            }

            // Verificar se cliente disse que quer mudar algo mas nÃ£o especificou
            var textoLower = mensagemTexto.ToLower();
            var querMudarHorario = (textoLower.Contains("horÃ¡rio") || textoLower.Contains("horario") ||
                                    textoLower.Contains("hora")) && novoHorario == null;
            var querMudarQtd = (textoLower.Contains("pessoa") || textoLower.Contains("gente") ||
                                textoLower.Contains("quantidade")) && !novaQtd.HasValue;
            var querMudarData = (textoLower.Contains("data") || textoLower.Contains("dia")) &&
                                !novaData.HasValue && !dadosContexto.ContainsKey("data_especificada");

            _logger.LogDebug(
                "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] Flags: querMudarHorario={Horario}, querMudarQtd={Qtd}, querMudarData={Data}",
                idConversa, codigoReserva, querMudarHorario, querMudarQtd, querMudarData);

            // Se disse que quer mudar mas nÃ£o especificou, perguntar
            if (querMudarHorario)
            {
                _logger.LogInformation(
                    "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] Cliente quer mudar HORÃRIO mas nÃ£o especificou",
                    idConversa, codigoReserva);

                var msg = $"â° HorÃ¡rio atual: {reserva.HoraInicio:hh\\:mm}\n\n" +
                          $"Qual o novo horÃ¡rio? ðŸ˜Š\n" +
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
                    "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] Cliente quer mudar QUANTIDADE mas nÃ£o especificou",
                    idConversa, codigoReserva);

                var msg = $"ðŸ‘¥ Quantidade atual: {reserva.QtdPessoas} pessoas\n\n" +
                          $"Quantas pessoas agora? ðŸ˜Š\n" +
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
                    "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] Cliente quer mudar DATA mas nÃ£o especificou - perguntando",
                    idConversa, codigoReserva);

                var msg = new StringBuilder();
                msg.AppendLine($"ðŸ“… Data atual da reserva #{reserva.Codigo}:");
                msg.AppendLine(DateFormattingHelper.FormatarDataCurta(reserva.DataReserva));
                msg.AppendLine();
                msg.AppendLine("Qual a nova data que vocÃª prefere? ðŸ™‚");
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
                    "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] Perguntando nova data ao usuÃ¡rio",
                    idConversa, codigoReserva);

                return (true, new AssistantDecision(reply, "none", null, false, null, null));
            }

            // Se nÃ£o houve mudanÃ§a E nÃ£o estÃ¡ pedindo algo especÃ­fico, nÃ£o intercepta
            if (!houveMudanca)
            {
                _logger.LogInformation(
                    "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] Nenhuma mudanÃ§a detectada - deixando IA processar",
                    idConversa, codigoReserva);
                return (false, null);
            }

            // Construir resumo das alteraÃ§Ãµes
            _logger.LogInformation(
                "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] MudanÃ§as coletadas - montando confirmaÃ§Ã£o",
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
                dataFinal?.ToString("yyyy-MM-dd") ?? "mantÃ©m",
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

            // Salvar contexto com estado de confirmaÃ§Ã£o
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
                "[ProcessarDadosAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] âœ… FIM - Aguardando confirmaÃ§Ã£o",
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
                    _logger.LogWarning(ex, "[ProcessarConfirmacaoAlteracaoAsync][Conversa={Conversa}][Reserva=#{Codigo}] Falha ao carregar reserva para confirmaÃ§Ã£o", idConversa);
                    codigoReserva = idReservaContexto.ToString();
                }
            }

            // ? DETECÃ‡ÃƒO ULTRA-COMPLETA DE CONFIRMAÃ‡Ã•ES (100+ variaÃ§Ãµes)
            var confirmacoesExatas = new HashSet<string>
    {
        "sim", "s", "ss", "ok", "okay", "oki", "oky",
        "blz", "beleza", "show", "suave", "massa", "top", "demais", "perfeito",
        "isso", "certeza", "certo", "positivo", "afirmativo",
        "tmj", "vamo", "bora", "dale", "valeu", "fechou", "fexa", "firmeza",
        "tranquilo", "tranks", "de boa", "partiu", "simbora",
        "aham", "uhum", "ahan", "sim sim", "sisim", "simsim",
        "sÃ´", "Ã´", "opa", "bÃ£o", "daora", "dahora",
        "pode crer", "ta valendo", "tÃ¡ valendo", "manda ver", "manda bala",
        "ðŸ‘", "ðŸ‘Œ", "ðŸ‘", "ðŸ˜„", "ðŸ˜", "ðŸ™‚", "ðŸ™Œ"
    };

            var confirmacoesContains = new[]
            {
        "eu confirmo","confirma", "confirmo", "isso mesmo", "isso aÃ­", "isso ai",
        "Ã© isso", "exato", "exatamente", "correto", "certinho",
        "pode sim", "pode ir", "pode mandar", "pode fazer",
        "tudo bem", "tudo certo", "tÃ¡ bom", "tÃ¡ ok", "ta bom", "ta ok",
        "estÃ¡ bom", "estÃ¡ ok", "com certeza", "claro", "Ã³bvio", "obvio",
        "lÃ³gico", "logico", "autorizo", "aprovado", "aprovo",
        "de acordo", "acordo", "concordo", "sem problema", "ðŸ‘", "ðŸ‘Œ", "ðŸ™Œ"
    };

            var ehConfirmacao = confirmacoesExatas.Contains(textoNorm) ||
                                confirmacoesContains.Any(c => textoNorm.Contains(c));

            // ? NOVO: Detectar se Ã© confirmaÃ§Ã£o MAS com mudanÃ§a adicional
            var temMudancaAdicional = textoNorm.Contains("tbm") ||
                                       textoNorm.Contains("tambÃ©m") ||
                                       textoNorm.Contains("tambem") ||
                                       (textoNorm.Contains(" e ") &&
                                        (textoNorm.Contains("quero") || textoNorm.Contains("mudar") || textoNorm.Contains("alterar")));

            // ? EXECUTAR: Chamar tool diretamente quando confirma
            if (ehConfirmacao)
            {
                _logger.LogInformation(
                    "[Conversa={Conversa}] ConfirmaÃ§Ã£o detectada: '{Texto}' - Executando atualizaÃ§Ã£o via tool",
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
                        "[Conversa={Conversa}] Erro ao executar tool atualizar_reserva apÃ³s confirmaÃ§Ã£o",
                        idConversa);

                    // Limpar contexto em caso de erro
                    await _conversationRepository.LimparContextoAsync(idConversa);

                    var erroMsg = "Ops! Tive um problema ao processar a confirmaÃ§Ã£o ðŸ˜µâ€ðŸ’«\n\nPode tentar novamente?";
                    return (true, new AssistantDecision(erroMsg, "none", null, false, null, null));
                }
            }
            else if (textoNorm.Contains("nÃ£o") || textoNorm.Contains("nao") || textoNorm == "n")
            {
                await _conversationRepository.LimparContextoAsync(idConversa);
                var reply = "Tudo bem! Sua reserva permanece como estava. Se precisar de algo, estou aqui! ðŸ¤—";
                await SalvarMensagemRespostaAsync(idConversa, reply);

                return (true, new AssistantDecision(reply, "none", null, false, null, null));
            }

            // NÃ£o conseguiu interpretar confirmaÃ§Ã£o, nÃ£o intercepta
            return (false, null);
        }

        private int? ExtrairNumeroEscolha(string texto)
        {
            texto = texto.ToLower().Trim();

            // NÃºmero direto
            if (int.TryParse(texto, out var numero))
                return numero;

            // Palavras
            var mapa = new Dictionary<string, int>
            {
                { "primeiro", 1 }, { "primeira", 1 }, { "um", 1 }, { "1", 1 },
                { "segundo", 2 }, { "segunda", 2 }, { "dois", 2 }, { "2", 2 },
                { "terceiro", 3 }, { "terceira", 3 }, { "tres", 3 }, { "trÃªs", 3 }, { "3", 3 },
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
            // Nota: A mensagem serÃ¡ salva e enviada pelo IAResponseHandler
            // Este mÃ©todo apenas registra que o contexto gerou uma resposta
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
                    "[Conversa={Conversa}] ProcessarAlteracaoDiretaAsync: NÃ£o conseguiu extrair data de '{Texto}'",
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

            // ? NOVO: Se nÃ£o tem mudanÃ§a especificada, pedir os dados
            if (novoHorario == null && !qtd.HasValue)
            {
                _logger.LogInformation(
                    "[Conversa={Conversa}] Reserva encontrada mas sem mudanÃ§a especificada - pedindo dados",
                    idConversa);

                // ? CORREÃ‡ÃƒO: Usar NomeCliente da reserva (nome informado no momento da reserva)
                var nomeReserva = alvo.NomeCliente ?? "Cliente";

                var msg = new StringBuilder();
                msg.AppendLine($"ðŸ“‹ Reserva #{alvo.Codigo} encontrada:");
                msg.AppendLine();
                msg.AppendLine($"ðŸ‘¤ Nome: {nomeReserva}");
                msg.AppendLine($"ðŸ“… Data: {DateFormattingHelper.FormatarDataCurta(alvo.DataReserva)}");
                msg.AppendLine($"â° HorÃ¡rio: {alvo.HoraInicio:hh\\:mm}");
                msg.AppendLine($"ðŸ‘¥ Pessoas: {alvo.QtdPessoas}");
                msg.AppendLine();
                msg.AppendLine("O que vocÃª quer alterar? ðŸ™‚");
                msg.AppendLine("â€¢ HorÃ¡rio (ex: 20h, 19:30)");
                msg.AppendLine("â€¢ Quantidade (ex: 8 pessoas, adicionar 2)");

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
                null,  // ? dataDepois (null = mantÃ©m data atual)
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
                ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)  // ? Aumentado de 10 para 30 minutos
            });

            await SalvarMensagemRespostaAsync(idConversa, replyConfirmacao);
            return (true, new AssistantDecision(replyConfirmacao, "none", null, false, null, null));
        }

        // agora com Ã¢ncora opcional: se informada, usar como base quando for "dia 12", "dd/MM" ou dia da semana
        private DateTime? ExtrairDataPreferencial(string texto, DateTime baseReferencia, DateTime? ancora = null)
        {
            if (string.IsNullOrWhiteSpace(texto)) return null;

            var referencia = baseReferencia.Date;
            var baseAncora = ancora?.Date;

            var norm = RemoveDiacritics(texto.ToLower()).Replace("-feira", "").Trim();

            // âœ… LOG PARA DEBUG
            _logger.LogDebug(
                "[ExtrairDataPreferencial] Input: '{Texto}' | Normalizado: '{Norm}' | Base: {Base:yyyy-MM-dd} | Ancora: {Ancora}",
                texto, norm, referencia, baseAncora?.ToString("yyyy-MM-dd") ?? "null");

            // 1. TERMOS RELATIVOS (prioridade mÃ¡xima)
            if (norm == "hoje")
            {
                _logger.LogDebug("[ExtrairDataPreferencial] âœ… HOJE -> {Data:yyyy-MM-dd}", referencia);
                return referencia;
            }

            if (norm.Contains("depois") && norm.Contains("amanha"))
            {
                var depoisAmanha = referencia.AddDays(2);
                _logger.LogDebug("[ExtrairDataPreferencial] âœ… DEPOIS DE AMANHÃƒ -> {Data:yyyy-MM-dd}", depoisAmanha);
                return depoisAmanha;
            }

            if (norm.Contains("amanha"))
            {
                var amanha = referencia.AddDays(1);
                _logger.LogDebug("[ExtrairDataPreferencial] âœ… AMANHÃƒ -> {Data:yyyy-MM-dd}", amanha);
                return amanha;
            }

            // 2. FORMATOS ABSOLUTOS (dd/MM/yyyy)
            if (DateTime.TryParseExact(norm, "dd/MM/yyyy",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dataCompleta))
            {
                _logger.LogDebug("[ExtrairDataPreferencial] âœ… dd/MM/yyyy -> {Data:yyyy-MM-dd}", dataCompleta);
                return dataCompleta.Date;
            }

            // 3. FORMATO dd/MM (assume ano da Ã¢ncora ou da referÃªncia)
            if (DateTime.TryParseExact(norm, "dd/MM",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parcial))
            {
                var ano = (baseAncora ?? referencia).Year;
                var tentativa = new DateTime(ano, parcial.Month, parcial.Day).Date;

                // Se a data jÃ¡ passou no ano atual, vai para o prÃ³ximo ano
                if (tentativa < referencia)
                {
                    tentativa = tentativa.AddYears(1);
                }

                _logger.LogDebug("[ExtrairDataPreferencial] âœ… dd/MM -> {Data:yyyy-MM-dd}", tentativa);
                return tentativa;
            }

            // 4. âœ… CRÃTICO: "DIA X" ou nÃºmeros isolados usando helper
            _logger.LogDebug("[ExtrairDataPreferencial] Tentando extrair dia via DateParsingHelper...");

            if (DateParsingHelper.TryExtractDayNumber(norm, out var diaExtraido))
            {
                _logger.LogDebug("[ExtrairDataPreferencial] Helper retornou dia: {Dia}", diaExtraido);

                var mesAtual = referencia.Month;
                var anoAtual = referencia.Year;

                // Se o dia jÃ¡ passou no mÃªs atual, vai para o prÃ³ximo mÃªs
                if (diaExtraido < referencia.Day)
                {
                    _logger.LogDebug(
                        "[ExtrairDataPreferencial] Dia {Dia} < dia atual {DiaAtual}, avanÃ§ando para prÃ³ximo mÃªs",
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
                        "[ExtrairDataPreferencial] âœ… DIA DETECTADO via helper: {Data:yyyy-MM-dd} (entrada: '{Texto}')",
                        dataCalculada, texto);
                    return dataCalculada;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "[ExtrairDataPreferencial] âŒ Dia invÃ¡lido: {Dia}/{Mes}/{Ano}",
                        diaExtraido, mesAtual, anoAtual);
                    return null;
                }
            }
            else
            {
                _logger.LogDebug("[ExtrairDataPreferencial] Helper nÃ£o conseguiu extrair dia numÃ©rico");
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
                        "[ExtrairDataPreferencial] âœ… Dia da semana '{DiaSemana}' -> {Data:yyyy-MM-dd}",
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
                _logger.LogDebug("[ExtrairDataPreferencial] âœ… Parse livre PT-BR -> {Data:yyyy-MM-dd}", livre);
                return livre.Date;
            }

            // âŒ NÃƒO CONSEGUIU PARSEAR
            _logger.LogWarning(
                "[ExtrairDataPreferencial] âŒ NÃƒO CONSEGUIU parsear nenhum formato: '{Texto}' (normalizado: '{Norm}')",
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
            // Nota: codigoReserva aqui representa o Id (long). Em fases futuras vamos buscar o CÃ³digo (string) no banco.
            var codigoExibicao = codigoReserva.ToString();
            var ptbr = new System.Globalization.CultureInfo("pt-BR");
            var sb = new StringBuilder();
            sb.AppendLine($"ðŸ“‹ Reserva #{codigoExibicao} - Confirme as alteraÃ§Ãµes:");
            sb.AppendLine();

            sb.AppendLine("ðŸ“… DATA:");
            if (dataDepois.HasValue && dataDepois.Value.Date != dataAntes.Date)
            {
                sb.AppendLine($"â†© Antes: {dataAntes:dd/MM/yyyy} ({dataAntes.ToString("dddd", ptbr)})");
                sb.AppendLine($"âž¡ Depois: {dataDepois.Value:dd/MM/yyyy} ({dataDepois.Value.ToString("dddd", ptbr)})");
            }
            else
            {
                sb.AppendLine($"âœ” MantÃ©m: {dataAntes:dd/MM/yyyy} ({dataAntes.ToString("dddd", ptbr)})");
            }
            sb.AppendLine();

            sb.AppendLine("â° HORÃRIO:");
            if (horaDepois == horaAntes)
            {
                sb.AppendLine($"âœ” MantÃ©m: {horaAntes}");
            }
            else
            {
                sb.AppendLine($"â†© Antes: {horaAntes}");
                sb.AppendLine($"âž¡ Depois: {horaDepois}");
            }
            sb.AppendLine();

            sb.AppendLine("ðŸ‘¥ PESSOAS:");
            if (qtdDepois == qtdAntes)
            {
                sb.AppendLine($"âœ” MantÃ©m: {qtdAntes}");
            }
            else
            {
                sb.AppendLine($"â†© Antes: {qtdAntes}");
                sb.AppendLine($"âž¡ Depois: {qtdDepois}");
            }
            sb.AppendLine();

            sb.AppendLine("Confirmar essas mudanÃ§as? âœ…");
            return sb.ToString();
        }

        /// <summary>
        /// Extrai cÃ³digo de reserva (4 dÃ­gitos) da mensagem.
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
                _logger.LogDebug("[ExtrairCodigoReserva] CÃ³digo extraÃ­do com #: {Codigo}", codigo);
                return codigo;
            }

            match = Regex.Match(textoNorm, "(?:codigo|cÃ³digo|reserva|resÃ©rva)\\s*(\\d{4})\\b");
            if (match.Success)
            {
                var codigo = match.Groups[1].Value;
                _logger.LogDebug("[ExtrairCodigoReserva] CÃ³digo extraÃ­do apÃ³s palavra-chave: {Codigo}", codigo);
                return codigo;
            }

            match = Regex.Match(textoNorm, "\\b(\\d{4})\\b");
            if (match.Success)
            {
                var codigo = match.Groups[1].Value;
                _logger.LogDebug("[ExtrairCodigoReserva] CÃ³digo isolado extraÃ­do: {Codigo}", codigo);
                return codigo;
            }

            _logger.LogDebug("[ExtrairCodigoReserva] Nenhum cÃ³digo encontrado em: '{Texto}'", texto);
            return null;
        }

        /// <summary>
        /// Extrai letra de escolha (A, B, C...) da mensagem.
        /// </summary>
        private string? ExtrairOpcaoLetra(string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return null;

            var textoNorm = texto.ToUpperInvariant().Trim();
            var match = Regex.Match(textoNorm, @"(?:^|[^A-Z])([A-Z])(?:[^A-Z]|$)");
            if (match.Success)
            {
                var letra = match.Groups[1].Value;
                _logger.LogDebug("[ExtrairOpcaoLetra] Letra extraÃ­da: {Letra}", letra);
                return letra;
            }

            _logger.LogDebug("[ExtrairOpcaoLetra] Nenhuma letra encontrada em: '{Texto}'", texto);
            return null;
        }

        /// <summary>
        /// Converte letra de opÃ§Ã£o para Ã­ndice de lista (A=0, B=1, ...).
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

            _logger.LogDebug("[MapearLetraParaIndice] Letra {Letra} â†’ Ãndice {Indice}", letra, indice);
            return indice;
        }

        /// <summary>
        /// Extrai data no formato dd/MM da mensagem.
        /// Se a data jÃ¡ passou neste ano, assume o prÃ³ximo ano.
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
                _logger.LogWarning("[ExtrairDataReserva] Data invÃ¡lida: {Dia}/{Mes}", dia, mes);
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

                _logger.LogDebug("[ExtrairDataReserva] Data extraÃ­da: {Data:yyyy-MM-dd} (texto='{Texto}')", data, texto);
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
            msg.AppendLine("ðŸ“‹ Suas reservas ativas:");
            msg.AppendLine();

            char opcao = 'A';
            foreach (var r in reservas)
            {
                msg.AppendLine($"OpÃ§Ã£o {opcao} - Reserva #{r.Codigo}");
                msg.AppendLine($"ðŸ“… {DateFormattingHelper.FormatarDataCurta(r.DataReserva)} Ã s {r.HoraInicio.ToString(@"hh\:mm")}");
                msg.AppendLine($"ðŸ‘¥ {r.QtdPessoas} pessoas");
                msg.AppendLine();
                opcao++;
            }

            msg.AppendLine("â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”");
            msg.AppendLine("Qual vocÃª quer alterar?");

            if (reservas.Count > 0)
            {
                msg.AppendLine($"Digite: A ou #{reservas[0].Codigo} ou {reservas[0].DataReserva:dd/MM}");
            }

            msg.AppendLine();
            msg.AppendLine("â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”");
            msg.AppendLine("Outras opÃ§Ãµes:");
            msg.AppendLine("1ï¸âƒ£ Fazer nova reserva");
            msg.AppendLine("2ï¸âƒ£ Encerrar atendimento");

            return msg.ToString();
        }

        private bool MensagemContemFiltro(string mensagem)
        {
            if (string.IsNullOrWhiteSpace(mensagem))
                return false;

            var textoLower = mensagem.ToLower();

            // Detectar cÃ³digo (#16, "cÃ³digo 16", "reserva 16")
            if (Regex.IsMatch(textoLower,
                @"#\d+|c[oÃ³]digo\s*\d+|reserva\s*\d+"))
            {
                _logger.LogInformation("[ContextInterceptor] Filtro detectado: CÃ“DIGO");
                return true;
            }

            // Detectar dia especÃ­fico ("dia 15", "15/10")
            if (Regex.IsMatch(textoLower,
                @"dia\s*\d{1,2}|\d{1,2}/\d{1,2}|\d{1,2}\s+de\s+\w+"))
            {
                _logger.LogInformation("[ContextInterceptor] Filtro detectado: DIA ESPECÃFICO");
                return true;
            }

            // Detectar dia da semana
            var diasSemana = new[] { "domingo", "segunda", "terÃ§a", "terca",
                "quarta", "quinta", "sexta", "sÃ¡bado", "sabado" };
            if (diasSemana.Any(dia => textoLower.Contains(dia)))
            {
                _logger.LogInformation("[ContextInterceptor] Filtro detectado: DIA DA SEMANA");
                return true;
            }

            // Detectar referÃªncia temporal
            if (textoLower.Contains("hoje") || textoLower.Contains("amanhÃ£") ||
                textoLower.Contains("amanha"))
            {
                _logger.LogInformation("[ContextInterceptor] Filtro detectado: TEMPORAL");
                return true;
            }

            // Detectar mÃªs
            var meses = new[] { "janeiro", "fevereiro", "marÃ§o", "marco",
                "abril", "maio", "junho", "julho", "agosto", "setembro",
                "outubro", "novembro", "dezembro" };
            if (meses.Any(mes => textoLower.Contains(mes)))
            {
                _logger.LogInformation("[ContextInterceptor] Filtro detectado: MÃŠS");
                return true;
            }

            return false;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
