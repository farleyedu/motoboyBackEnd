// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
// ✨ MUDANÇAS PRINCIPAIS:
// 1. HandleCancelarReserva: Adiciona LimparContextoAsync após cancelar
// 2. HandleConfirmarReserva: Adiciona LimparContextoAsync NO INÍCIO
// 3. HandleListarReservas: Já estava OK (filtra status=Confirmado)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Helpers;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using APIBack.Model;
using APIBack.Repository.Interface;
using APIBack.Service;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class ConfirmarReservaArgs
    {
        public Guid IdConversa { get; set; }
        public string NomeCompleto { get; set; } = string.Empty;
        public int QtdPessoas { get; set; }
        public string Data { get; set; } = string.Empty;
        public string Hora { get; set; } = string.Empty;
    }

    public class EscalarParaHumanoArgs
    {
        public Guid IdConversa { get; set; }
        public string Motivo { get; set; } = string.Empty;
        public string ResumoConversa { get; set; } = string.Empty;
    }

    public class CancelarReservaArgs
    {
        public Guid IdConversa { get; set; }
        public long? CodigoReserva { get; set; }
        public string MotivoCliente { get; set; } = string.Empty;
    }

    public class AtualizarReservaArgs
    {
        public Guid IdConversa { get; set; }
        public long? CodigoReserva { get; set; }
        public string? FiltroData { get; set; }        // Data mencionada: "dia 11", "15/10", "sexta"
        public string? NovaData { get; set; }          // ⬅️ NOVO: aceitar mudança de DATA (texto do cliente, sem formatar)
        public string? NovoHorario { get; set; }
        public int? NovaQtdPessoas { get; set; }
        public bool? EhMudancaRelativa { get; set; }   // true = "adicionar/tirar", false = número absoluto
    }

    public class ToolExecutorService
    {
        private const string MissingReservationDataMessage = "Para organizar a sua reserva, preciso de algumas informações:\n\n📋 Nome completo\n👥 Número de pessoas\n📅 Data\n⏰ Horário\n\nPode me passar esses dados? 😊";

        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly ILogger<ToolExecutorService> _logger;
        private readonly IConversationRepository _conversationRepository;
        private readonly HandoverService _handoverService;
        private readonly IReservaRepository _reservaRepository;
        private readonly ReservaValidator _reservaValidator;
        private readonly IClienteRepository _clienteRepository;

        public ToolExecutorService(
            ILogger<ToolExecutorService> logger,
            IConversationRepository conversationRepository,
            HandoverService handoverService,
            IReservaRepository reservaRepository,
            ReservaValidator reservaValidator,
            IClienteRepository clienteRepository)
        {
            _logger = logger;
            _conversationRepository = conversationRepository;
            _handoverService = handoverService;
            _reservaRepository = reservaRepository;
            _reservaValidator = reservaValidator;
            _clienteRepository = clienteRepository;
        }

        public object[] GetDeclaredTools(Guid idConversa)
        {
            var idConversaString = idConversa.ToString();
            return new object[]
            {
                new {
                    type = "function",
                    name = "confirmar_reserva",
                    description = "Confirma uma reserva após ter todos os dados e a confirmação explícita do usuário. SEMPRE verifica se já existe reserva antes.",
                    parameters = new {
                        type = "object",
                        properties = new {
                            idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                            nomeCompleto = new { type = "string", description = "Nome completo do cliente para a reserva" },
                            qtdPessoas = new { type = "integer", description = "Quantidade de pessoas na reserva" },
                            data = new { type = "string", description = "Data da reserva conforme informado pelo usuário (ex: 'amanhã', '25/12/2025'). Não calcule nem formate." },
                            hora = new { type = "string", description = "Horário da reserva no formato HH:mm" }
                        },
                        required = new[] { "idConversa", "nomeCompleto", "qtdPessoas", "data", "hora" }
                    }
                },
                new {
                    type = "function",
                    name = "listar_reservas",
                    description = @"Lista reservas ativas do cliente.

QUANDO USAR:
- Cliente pediu para alterar/cancelar MAS tem múltiplas reservas
- Cliente não especificou qual reserva quer alterar
- Cliente pediu explicitamente para ver suas reservas

IMPORTANTE: Após listar, aguarde cliente escolher uma antes de atualizar.",
                    parameters = new {
                        type = "object",
                        properties = new {
                            idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } }
                        },
                        required = new[] { "idConversa" }
                    }
                },
                new {
                    type = "function",
                    name = "cancelar_reserva",
                    description = "Cancela uma reserva existente do cliente. Se cliente mencionar código (#23) ou número específico, use codigoReserva. Só executar após confirmação explícita do cliente.",
                    parameters = new {
                        type = "object",
                        properties = new {
                            idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                            codigoReserva = new { type = "integer", description = "Código da reserva. Extraia de: '#25', 'código 25', 'reserva 25', 'é a 25', 'a 25', '25' (número solto após pergunta), 'o 25', 'número 25'. Se cliente responde com número após você perguntar 'qual reserva', SEMPRE envie aqui." },
                            motivoCliente = new { type = "string", description = "Breve motivo do cancelamento informado pelo cliente" }
                        },
                        required = new[] { "idConversa" }
                    }
                },
                new {
                    type = "function",
                    name = "atualizar_reserva",
                    description = @"Atualiza uma reserva existente.

QUANDO USAR:
- Cliente tem 1 reserva E mencionou mudança (horário/quantidade/data)
- Cliente mencionou código (#123) explicitamente
- Cliente mencionou filtro claro (dia 11, sexta-feira, 15/10)

QUANDO NÃO USAR:
- Cliente tem múltiplas reservas SEM especificar qual
- Nesse caso, chame 'listar_reservas' PRIMEIRO

PARÂMETROS IMPORTANTES:
- codigoReserva: SEMPRE envie se cliente mencionou número
- filtroData: Envie texto exato do cliente (não formate)
- novoHorario: Formato HH:mm
- novaQtdPessoas: Número absoluto ou relativo (veja ehMudancaRelativa)",
                    parameters = new {
                        type = "object",
                        properties = new {
                            idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                            codigoReserva = new { type = "integer", description = "Código (#123) quando o cliente forneceu explicitamente ou respondeu número após você perguntar 'qual reserva'. SEMPRE envie aqui." },
                            filtroData = new { type = "string", description = "Filtro textual de data fornecido pelo cliente: 'dia 11', '15/10', 'sexta', 'amanhã'..." },
                            novaData = new { type = "string", description = "⬅️ NOVO: nova data textual informada pelo cliente (não formate). Ex.: 'dia 12', '12/11', 'sexta'." },
                            novoHorario = new { type = "string", description = "Novo horário HH:mm (ex.: 19:00) quando o cliente quer alterar horário" },
                            novaQtdPessoas = new { type = "integer", description = "Nova quantidade de pessoas quando o cliente quer alterar quantidade" },
                            ehMudancaRelativa = new { type = "boolean", description = "true=adicionar/tirar pessoas, false=valor absoluto" }
                        },
                        required = new[] { "idConversa" }
                    }
                },
                new {
                    type = "function",
                    name = "escalar_para_humano",
                    description = "Transfere a conversa para um atendente humano. CRÍTICO: Só executar após confirmação EXPLÍCITA do cliente pedindo atendimento humano.",
                    parameters = new {
                        type = "object",
                        properties = new {
                            idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                            motivo = new { type = "string", description = "Breve explicação do motivo do escalonamento" },
                            resumoConversa = new { type = "string", description = "Resumo do que foi discutido" }
                        },
                        required = new[] { "idConversa", "motivo", "resumoConversa" }
                    }
                }
            };
        }

        private string BuildJsonReply(string reply, bool? reservaConfirmada = null)
        {
            if (reservaConfirmada.HasValue)
            {
                var objComConfirmacao = new { reply, reserva_confirmada = reservaConfirmada.Value };
                return JsonSerializer.Serialize(objComConfirmacao, JsonOptions);
            }

            var obj = new { reply };
            return JsonSerializer.Serialize(obj, JsonOptions);
        }

        public async Task<string> ExecuteToolAsync(string toolName, string argsJson)
        {
            try
            {
                if (argsJson.StartsWith("\"") && argsJson.EndsWith("\""))
                {
                    argsJson = JsonSerializer.Deserialize<string>(argsJson) ?? string.Empty;
                }

                switch (toolName)
                {
                    case "confirmar_reserva":
                        var reservaArgs = JsonSerializer.Deserialize<ConfirmarReservaArgs>(argsJson, JsonOptions);
                        if (reservaArgs == null)
                        {
                            return BuildJsonReply("Argumentos inválidos para confirmar reserva.");
                        }
                        return await HandleConfirmarReserva(reservaArgs);

                    case "listar_reservas":
                        var listarArgs = JsonSerializer.Deserialize<Dictionary<string, Guid>>(argsJson, JsonOptions);
                        if (listarArgs == null || !listarArgs.TryGetValue("idConversa", out var idConvLista))
                        {
                            return BuildJsonReply("Argumentos inválidos.");
                        }
                        return await HandleListarReservas(idConvLista);

                    case "cancelar_reserva":
                        var cancelarArgs = JsonSerializer.Deserialize<CancelarReservaArgs>(argsJson, JsonOptions);
                        if (cancelarArgs == null)
                        {
                            return BuildJsonReply("Argumentos inválidos para cancelar reserva.");
                        }
                        return await HandleCancelarReserva(cancelarArgs);

                    case "atualizar_reserva":
                        var atualizarArgs = JsonSerializer.Deserialize<AtualizarReservaArgs>(argsJson, JsonOptions);
                        if (atualizarArgs == null)
                        {
                            return BuildJsonReply("Argumentos inválidos para atualizar reserva.");
                        }
                        return await HandleAtualizarReserva(atualizarArgs);

                    case "escalar_para_humano":
                        var escalarArgs = JsonSerializer.Deserialize<EscalarParaHumanoArgs>(argsJson, JsonOptions);
                        if (escalarArgs == null)
                        {
                            return BuildJsonReply("Argumentos inválidos para escalar ao atendimento.");
                        }
                        return await HandleEscalarParaHumano(escalarArgs);

                    default:
                        _logger.LogWarning("Ferramenta desconhecida: {Tool}", toolName);
                        return BuildJsonReply($"Ferramenta {toolName} não implementada.");
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Erro ao fazer parse dos argumentos da ferramenta {Tool}: {Json}", toolName, argsJson);
                return BuildJsonReply("Erro ao processar os argumentos da ferramenta.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro inesperado ao executar ferramenta {Tool}", toolName);
                return BuildJsonReply($"Erro ao executar {toolName}: {ex.Message}");
            }
        }

        private async Task<string> HandleConfirmarReserva(ConfirmarReservaArgs args)
        {
            args.NomeCompleto = args.NomeCompleto?.Trim() ?? string.Empty;
            args.Data = args.Data?.Trim() ?? string.Empty;
            args.Hora = args.Hora?.Trim() ?? string.Empty;

            // ✨ ADICIONADO: Limpar contexto antigo ANTES de criar nova reserva
            _logger.LogInformation(
                "[Conversa={Conversa}] Iniciando confirmação de NOVA reserva. Limpando contexto antigo.",
                args.IdConversa);

            await _conversationRepository.LimparContextoAsync(args.IdConversa);

            var validationResult = await _reservaValidator.ValidateReservaAsync(
                args.IdConversa,
                args.NomeCompleto,
                args.QtdPessoas,
                args.Data,
                args.Hora);

            if (!validationResult.IsValid)
            {
                _logger.LogWarning(
                    "[Conversa={Conversa}] Validação preventiva falhou: {Issue}",
                    args.IdConversa,
                    validationResult.Issue);

                if (validationResult.Issue == ReservaValidationIssue.DuplicacaoMesmoDia)
                {
                    return BuildJsonReply(validationResult.MensagemErro!, reservaConfirmada: false);
                }

                return BuildJsonReply(validationResult.MensagemErro!);
            }

            var dataReserva = validationResult.DataCalculada!.Value;
            var horaConvertida = validationResult.HoraCalculada!.Value;

            var conversa = await _conversationRepository.ObterPorIdAsync(args.IdConversa);
            if (conversa == null)
            {
                _logger.LogWarning("[Conversa={Conversa}] Conversa não encontrada ao confirmar reserva", args.IdConversa);
                return BuildJsonReply("Não consegui localizar nossa conversa agora.\n\nPode tentar novamente em instantes? 😊");
            }

            if (string.IsNullOrWhiteSpace(conversa.TelefoneCliente))
            {
                _logger.LogWarning("[Conversa={Conversa}] Telefone não encontrado para confirmação de reserva", args.IdConversa);
                return BuildJsonReply("Desculpe, não consegui identificar seu telefone.\n\nPode me chamar de novo para finalizar? 😊");
            }

            var telefone = conversa.TelefoneCliente;
            var idCliente = conversa.IdCliente;
            var idEstabelecimento = conversa.IdEstabelecimento;

            var reservasExistentes = await _reservaRepository.ObterPorClienteEstabelecimentoAsync(idCliente, idEstabelecimento);
            var referenciaAtual = TimeZoneHelper.GetSaoPauloNow();
            var reservasAtivas = reservasExistentes
                .Where(r => r.Status == ReservaStatus.Confirmado && r.DataReserva >= referenciaAtual.Date)
                .ToList();

            var reservaMesmoDia = reservasAtivas
                .Where(r => r.DataReserva.Date == dataReserva.Date)
                .OrderByDescending(r => r.DataAtualizacao)
                .FirstOrDefault();

            long idReserva;
            bool ehAtualizacao = false;

            if (reservaMesmoDia != null)
            {
                ehAtualizacao = true;
                idReserva = reservaMesmoDia.Id;

                reservaMesmoDia.QtdPessoas = args.QtdPessoas;
                reservaMesmoDia.HoraInicio = horaConvertida;
                reservaMesmoDia.DataAtualizacao = DateTime.UtcNow;

                await _reservaRepository.AtualizarAsync(reservaMesmoDia);

                _logger.LogInformation(
                    "[Conversa={Conversa}] Reserva #{ReservaId} ATUALIZADA: {Nome}, {Qtd} pessoas, {Data} às {Hora}",
                    args.IdConversa,
                    idReserva,
                    args.NomeCompleto,
                    args.QtdPessoas,
                    dataReserva.ToString("dd/MM/yyyy"),
                    horaConvertida.ToString(@"hh\:mm"));
            }
            else
            {
                var agoraUtc = DateTime.UtcNow;
                var reserva = new Reserva
                {
                    IdCliente = idCliente,
                    IdEstabelecimento = idEstabelecimento,
                    QtdPessoas = args.QtdPessoas,
                    DataReserva = dataReserva,
                    HoraInicio = horaConvertida,
                    Status = ReservaStatus.Confirmado,
                    DataCriacao = agoraUtc,
                    DataAtualizacao = agoraUtc
                };

                idReserva = await _reservaRepository.AdicionarAsync(reserva);

                _logger.LogInformation(
                    "[Conversa={Conversa}] Reserva #{ReservaId} CRIADA: {Nome}, {Qtd} pessoas, {Data} às {Hora}",
                    args.IdConversa,
                    idReserva,
                    args.NomeCompleto,
                    args.QtdPessoas,
                    dataReserva.ToString("dd/MM/yyyy"),
                    horaConvertida.ToString(@"hh\:mm"));
            }

            var dataFormatada = dataReserva.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            var horaFormatada = horaConvertida.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

            var detalhesReserva = new HandoverContextDto
            {
                ClienteNome = args.NomeCompleto,
                NumeroPessoas = args.QtdPessoas.ToString(CultureInfo.InvariantCulture),
                Dia = dataFormatada,
                Horario = horaFormatada,
                Telefone = telefone
            };

            await _conversationRepository.AtualizarEstadoAsync(args.IdConversa, EstadoConversa.FechadoAutomaticamente);
            await _handoverService.ProcessarMensagensTelegramAsync(args.IdConversa, null, true, detalhesReserva);

            var builder = new StringBuilder();

            if (ehAtualizacao)
            {
                builder.AppendLine("✅ Sua reserva foi atualizada com sucesso! 🎉");
            }
            else
            {
                builder.AppendLine("🎉 Sua reserva está confirmadíssima! 🎉");
            }

            builder.AppendLine();
            builder.AppendLine($"Oi, {args.NomeCompleto}! Já estamos preparando um lugar especial para você e seus convidados.");
            builder.AppendLine();
            builder.AppendLine("Confira os dados do seu agendamento:");
            builder.AppendLine();
            builder.AppendLine($"📅 Data: {dataFormatada}");
            builder.AppendLine($"⏰ Horário: {horaFormatada}");
            builder.AppendLine($"👥 Pessoas: {args.QtdPessoas}");
            builder.AppendLine();

            if (validationResult.VagasDisponiveis.HasValue)
            {
                var vagasRestantes = validationResult.VagasDisponiveis.Value - args.QtdPessoas;
                if (vagasRestantes >= 0)
                {
                    builder.AppendLine($"📊 Vagas restantes neste dia: {vagasRestantes} pessoas");
                    builder.AppendLine();
                }
            }

            builder.AppendLine($"🎫 Seu código de reserva é o #{idReserva}.");
            builder.AppendLine("Caso precise alterar ou cancelar, é só nos informar este número para agilizar o atendimento!");
            builder.AppendLine();
            builder.AppendLine("⚠️ Atenção: Para que todos tenham uma ótima experiência, sua mesa ficará reservada por até 15 minutos após o horário marcado. Agradecemos a compreensão!");
            builder.AppendLine();
            builder.Append("Será um prazer receber vocês! ✨🥂");

            var reply = builder.ToString();
            return BuildJsonReply(reply, reservaConfirmada: true);
        }

        private async Task<string> HandleCancelarReserva(CancelarReservaArgs args)
        {
            args.MotivoCliente = args.MotivoCliente?.Trim() ?? "Não informado";

            var conversa = await _conversationRepository.ObterPorIdAsync(args.IdConversa);
            if (conversa == null)
            {
                return BuildJsonReply("Não consegui localizar nossa conversa.\n\nPode tentar novamente? 😊");
            }

            var idCliente = conversa.IdCliente;
            var idEstabelecimento = conversa.IdEstabelecimento;

            if (idCliente == Guid.Empty || idEstabelecimento == Guid.Empty)
            {
                return BuildJsonReply("Não consegui identificar seus dados.\n\nPode tentar novamente? 😊");
            }

            var reservasExistentes = await _reservaRepository.ObterPorClienteEstabelecimentoAsync(idCliente, idEstabelecimento);
            var referenciaAtual = TimeZoneHelper.GetSaoPauloNow();

            // ✨ JÁ ESTAVA OK: Filtra apenas reservas com status=Confirmado
            var reservasAtivas = reservasExistentes
                .Where(r => r.Status == ReservaStatus.Confirmado && r.DataReserva >= referenciaAtual.Date)
                .OrderBy(r => r.DataReserva)
                .ToList();

            if (!reservasAtivas.Any())
            {
                _logger.LogInformation("[Conversa={Conversa}] Cliente tentou cancelar mas não possui reservas ativas", args.IdConversa);
                return BuildJsonReply("Não encontrei nenhuma reserva ativa no seu nome 🤔\n\nSe precisar de ajuda, é só me avisar! 😊");
            }

            // ✨ NOVO: Se código foi fornecido, cancela diretamente
            if (args.CodigoReserva.HasValue)
            {
                var reservaPorCodigo = reservasAtivas.FirstOrDefault(r => r.Id == args.CodigoReserva.Value);

                if (reservaPorCodigo == null)
                {
                    _logger.LogWarning(
                        "[Conversa={Conversa}] Código #{Codigo} não encontrado nas reservas ativas do cliente",
                        args.IdConversa,
                        args.CodigoReserva.Value);

                    return BuildJsonReply($"Não encontrei a reserva #{args.CodigoReserva.Value} no seu nome. 😕\n\nQuer que eu liste suas reservas ativas? 😊");
                }

                await _reservaRepository.CancelarReservaAsync(reservaPorCodigo.Id);
                await _conversationRepository.LimparContextoAsync(args.IdConversa);

                var dataFormatada = reservaPorCodigo.DataReserva.ToString("dd/MM/yyyy");
                var horaFormatada = reservaPorCodigo.HoraInicio.ToString(@"hh\:mm");

                _logger.LogInformation(
                    "[Conversa={Conversa}] Reserva #{IdReserva} cancelada via código. Contexto limpo. Motivo: {Motivo}",
                    args.IdConversa,
                    reservaPorCodigo.Id,
                    args.MotivoCliente);

                var msg = new StringBuilder();
                msg.AppendLine("✅ Reserva cancelada com sucesso!");
                msg.AppendLine();
                msg.AppendLine($"🎫 Código: #{reservaPorCodigo.Id}");
                msg.AppendLine($"📅 Data: {dataFormatada}");
                msg.AppendLine($"⏰ Horário: {horaFormatada}");
                msg.AppendLine();
                msg.Append("Se mudar de ideia, estamos aqui! 😊");

                return BuildJsonReply(msg.ToString());
            }

            if (reservasAtivas.Count == 1)
            {
                var reserva = reservasAtivas.First();
                await _reservaRepository.CancelarReservaAsync(reserva.Id);

                // ✨ ADICIONADO: Limpar contexto após cancelar
                await _conversationRepository.LimparContextoAsync(args.IdConversa);

                var dataFormatada = reserva.DataReserva.ToString("dd/MM/yyyy");
                var horaFormatada = reserva.HoraInicio.ToString(@"hh\:mm");

                _logger.LogInformation(
                    "[Conversa={Conversa}] Reserva #{IdReserva} cancelada. Contexto limpo. Motivo: {Motivo}",
                    args.IdConversa,
                    reserva.Id,
                    args.MotivoCliente);

                var msg = new StringBuilder();
                msg.AppendLine("✅ Reserva cancelada com sucesso!");
                msg.AppendLine();
                msg.AppendLine($"📅 Data: {dataFormatada}");
                msg.AppendLine($"⏰ Horário: {horaFormatada}");
                msg.AppendLine();
                msg.Append("Se mudar de ideia, estamos aqui! 😊");

                return BuildJsonReply(msg.ToString());
            }

            var listaReservas = new StringBuilder();
            listaReservas.AppendLine("Você tem mais de uma reserva ativa:");
            listaReservas.AppendLine();
            foreach (var r in reservasAtivas)
            {
                var diaSemana = r.DataReserva.ToString("dddd", new CultureInfo("pt-BR"));
                listaReservas.AppendLine($"🎫 #{r.Id}");
                listaReservas.AppendLine($"📅 {r.DataReserva:dd/MM/yyyy} ({diaSemana})");
                listaReservas.AppendLine($"⏰ {r.HoraInicio:hh\\:mm}");
                listaReservas.AppendLine($"👥 {r.QtdPessoas} pessoas");
                listaReservas.AppendLine();
            }
            listaReservas.Append("Qual delas você quer cancelar? Informe o código (#) ou a data 😊");

            return BuildJsonReply(listaReservas.ToString());
        }

        private async Task<string> HandleAtualizarReserva(AtualizarReservaArgs args)
        {
            var conversa = await _conversationRepository.ObterPorIdAsync(args.IdConversa);
            if (conversa == null)
                return BuildJsonReply("Não consegui localizar nossa conversa.\n\nPode tentar novamente? 😊");

            var idCliente = conversa.IdCliente;
            var idEstabelecimento = conversa.IdEstabelecimento;

            // ============================================================
            // ✨ NOVO: VERIFICAR SE EXISTE CONTEXTO DE ALTERAÇÃO
            // ============================================================
            var contexto = await _conversationRepository.ObterContextoAsync(args.IdConversa);

            // ✅ CASO 1: Dados coletados pelo interceptor, precisa montar confirmação
            if (contexto?.Estado == "pronto_para_atualizar")
            {
                _logger.LogInformation(
                    "[Conversa={Conversa}] Contexto pronto_para_atualizar detectado - montando confirmação",
                    args.IdConversa);

                var resIdPronto = contexto.ReservaIdPendente ?? 0;
                if (resIdPronto == 0)
                {
                    _logger.LogError("[Conversa={Conversa}] ReservaIdPendente ausente no contexto", args.IdConversa);
                    await _conversationRepository.LimparContextoAsync(args.IdConversa);
                    return BuildJsonReply("Ocorreu um erro. Tente novamente! 😊");
                }

                var resPronto = await _reservaRepository.BuscarPorIdAsync(resIdPronto);
                if (resPronto == null)
                {
                    _logger.LogError("[Conversa={Conversa}] Reserva #{Id} não encontrada", args.IdConversa, resIdPronto);
                    await _conversationRepository.LimparContextoAsync(args.IdConversa);
                    return BuildJsonReply("Não encontrei a reserva. Pode tentar novamente? 😊");
                }

                // Pegar dados do contexto
                var horaContexto = contexto.DadosColetados?["novo_horario"]?.ToString() ?? "";
                var qtdContexto = int.Parse(contexto.DadosColetados?["nova_qtd"]?.ToString() ?? "0");

                // Montar mensagem de confirmação
                var dataPronto = resPronto.DataReserva.Date;
                var horaPronto = resPronto.HoraInicio.ToString(@"hh\:mm");
                var horaFinalPronto = !string.IsNullOrWhiteSpace(horaContexto) ? horaContexto : horaPronto;
                var qtdPronto = resPronto.QtdPessoas ?? 0;
                var qtdFinalPronto = qtdContexto > 0 ? qtdContexto : qtdPronto;

                var msgConfirmacao = BuildMsgConfirmacaoAlteracao(
                    resPronto.Id, dataPronto, dataPronto, horaPronto, horaFinalPronto, qtdPronto, qtdFinalPronto);

                // Atualizar estado para aguardando confirmação
                await _conversationRepository.SalvarContextoAsync(args.IdConversa, new ConversationContext
                {
                    Estado = "aguardando_confirmacao_alteracao",
                    ReservaIdPendente = resPronto.Id,
                    DadosColetados = new Dictionary<string, object>
                    {
                        { "reserva_id", resPronto.Id },
                        { "nova_data", dataPronto.ToString("yyyy-MM-dd") },
                        { "novo_horario", horaFinalPronto },
                        { "nova_qtd", qtdFinalPronto }
                    },
                    ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
                });

                return BuildJsonReply(msgConfirmacao);
            }

            // ✅ CASO 2: Já tem confirmação, executar atualização
            if (contexto?.Estado == "aguardando_confirmacao_alteracao")
            {
                _logger.LogInformation(
                    "[Conversa={Conversa}] Processando confirmação de alteração via tool",
                    args.IdConversa);

                // Pegar dados salvos no contexto
                var reservaIdConf = contexto.ReservaIdPendente ?? 0;
                if (reservaIdConf == 0)
                {
                    _logger.LogError("[Conversa={Conversa}] ReservaIdPendente ausente no contexto", args.IdConversa);
                    await _conversationRepository.LimparContextoAsync(args.IdConversa);
                    return BuildJsonReply("Ocorreu um erro ao processar a confirmação. Tente novamente! 😊");
                }

                var novaDataStr = contexto.DadosColetados?["nova_data"]?.ToString();
                var novoHorarioConf = contexto.DadosColetados?["novo_horario"]?.ToString();
                var novaQtdStr = contexto.DadosColetados?["nova_qtd"]?.ToString();

                // Buscar reserva
                var reservaConf = await _reservaRepository.BuscarPorIdAsync(reservaIdConf);
                if (reservaConf == null)
                {
                    _logger.LogError("[Conversa={Conversa}] Reserva #{Id} não encontrada", args.IdConversa, reservaIdConf);
                    await _conversationRepository.LimparContextoAsync(args.IdConversa);
                    return BuildJsonReply("Não encontrei a reserva. Pode tentar novamente? 😊");
                }

                // EXECUTAR ATUALIZAÇÃO
                bool alterouConf = false;

                // Atualizar DATA (se houver)
                if (!string.IsNullOrWhiteSpace(novaDataStr) && DateTime.TryParse(novaDataStr, out var novaDataConf))
                {
                    if (reservaConf.DataReserva.Date != novaDataConf.Date)
                    {
                        reservaConf.DataReserva = novaDataConf.Date;
                        alterouConf = true;
                    }
                }

                // Atualizar HORÁRIO (se houver)
                if (!string.IsNullOrWhiteSpace(novoHorarioConf) && TimeSpan.TryParseExact(novoHorarioConf, @"hh\:mm", null, out var tsConf))
                {
                    if (reservaConf.HoraInicio != tsConf)
                    {
                        reservaConf.HoraInicio = tsConf;
                        alterouConf = true;
                    }
                }

                // Atualizar QUANTIDADE (se houver)
                if (!string.IsNullOrWhiteSpace(novaQtdStr) && int.TryParse(novaQtdStr, out var novaQtdConf) && novaQtdConf > 0)
                {
                    if (reservaConf.QtdPessoas != novaQtdConf)
                    {
                        reservaConf.QtdPessoas = novaQtdConf;
                        alterouConf = true;
                    }
                }

                if (alterouConf)
                {
                    reservaConf.DataAtualizacao = DateTime.UtcNow;
                    await _reservaRepository.AtualizarAsync(reservaConf);

                    _logger.LogInformation(
                        "[Conversa={Conversa}] Reserva #{Id} ATUALIZADA com sucesso via tool",
                        args.IdConversa,
                        reservaConf.Id);
                }

                // LIMPAR CONTEXTO
                await _conversationRepository.LimparContextoAsync(args.IdConversa);

                // Retornar mensagem de sucesso
                var msgConf = new StringBuilder();
                msgConf.AppendLine("✅ Reserva atualizada com sucesso! 🎉");
                msgConf.AppendLine();
                msgConf.AppendLine($"🎫 Código: #{reservaConf.Id}");
                msgConf.AppendLine($"📅 Data: {reservaConf.DataReserva:dd/MM/yyyy}");
                msgConf.AppendLine($"⏰ Horário: {reservaConf.HoraInicio:hh\\:mm}");
                msgConf.AppendLine($"👥 Pessoas: {reservaConf.QtdPessoas}");
                msgConf.AppendLine();
                msgConf.Append("Nos vemos lá! ✨🥂");

                return BuildJsonReply(msgConf.ToString());
            }

            // ============================================================
            // FLUXO NORMAL: Localizar reserva e montar confirmação
            // ============================================================

            // ------------------------------------------------------------
            // 1) Localizar a reserva alvo (por código ou filtroData)
            // ------------------------------------------------------------
            APIBack.Model.Reserva? reserva;
            if (args.CodigoReserva.HasValue)
            {
                reserva = await _reservaRepository.BuscarPorIdAsync(args.CodigoReserva.Value);
            }
            else if (!string.IsNullOrWhiteSpace(args.FiltroData))
            {
                // reaproveite sua lógica existente de localizar por data
                var lista = await _reservaRepository.ObterPorClienteEstabelecimentoAsync(idCliente, idEstabelecimento);
                var futuras = lista.Where(r => r.Status == APIBack.Model.ReservaStatus.Confirmado &&
                                               r.DataReserva.Date.Add(r.HoraInicio) > TimeZoneHelper.GetSaoPauloNow())
                                   .OrderBy(r => r.DataReserva.Date.Add(r.HoraInicio))
                                   .ToList();
                // heurística simples: pega a primeira que bate com o filtro textual já usado no projeto (mantendo comportamento)
                reserva = futuras.FirstOrDefault();
            }
            else
            {
                return BuildJsonReply("Qual reserva você quer atualizar? Se preferir, me informe o código (#123).");
            }

            if (reserva == null)
                return BuildJsonReply("Não encontrei a reserva indicada. Pode me enviar o código (#123) ou a data dela?");

            // ------------------------------------------------------------
            // 2) Resolver NOVA DATA (se informada) usando ÂNCORA = data da reserva atual
            // ------------------------------------------------------------
            DateTime? novaDataCalculada = null;
            if (!string.IsNullOrWhiteSpace(args.NovaData))
            {
                novaDataCalculada = ResolverDataComAncora(args.NovaData!, reserva.DataReserva.Date, TimeZoneHelper.GetSaoPauloNow().Date);
                if (!novaDataCalculada.HasValue)
                    return BuildJsonReply("Não consegui entender a nova data. Pode mandar no formato 12/11, 'dia 12' ou 'próxima sexta'? 😊");
            }

            // ------------------------------------------------------------
            // 3) Validar regras gerais (reutilizando ReservaValidator)
            // ------------------------------------------------------------
            // Hora (se houver)
            TimeSpan? novoHorarioParsed = null;
            if (!string.IsNullOrWhiteSpace(args.NovoHorario))
            {
                if (!TimeSpan.TryParseExact(args.NovoHorario.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var horarioTemp))
                    return BuildJsonReply("Formato de horário inválido. Use HH:MM (ex.: 19:00)");

                // ✅ Validação simplificada de horário (11h-23h)
                if (horarioTemp.Hours < 11 || horarioTemp.Hours >= 23)
                    return BuildJsonReply("Esse horário está fora do nosso expediente (11h-23h). Quer tentar outro? 😊");

                novoHorarioParsed = horarioTemp;
            }

            // Qtd (se houver)
            int? novaQtdAbsoluta = null;
            if (args.NovaQtdPessoas.HasValue)
            {
                if (args.NovaQtdPessoas.Value <= 0 || args.NovaQtdPessoas.Value > 110)
                    return BuildJsonReply("Quantidade inválida. Trabalhamos com até 110 pessoas por dia. 😊");
                novaQtdAbsoluta = args.NovaQtdPessoas.Value;
            }

            // ✅ Validação simplificada
            var dataAlvo = (novaDataCalculada ?? reserva.DataReserva).Date;
            var horaAlvo = (novoHorarioParsed ?? reserva.HoraInicio);

            // Validar se data não é no passado
            var agora = TimeZoneHelper.GetSaoPauloNow();
            if (dataAlvo.Add(horaAlvo) < agora)
                return BuildJsonReply("Não podemos agendar para uma data/hora no passado. Escolha outra data! 😊");

            // Validar quantidade (validação extra, caso necessário)
            if (novaQtdAbsoluta.HasValue && (novaQtdAbsoluta.Value <= 0 || novaQtdAbsoluta.Value > 110))
                return BuildJsonReply("Quantidade inválida. Trabalhamos com até 110 pessoas por dia. 😊");

            // ------------------------------------------------------------
            // 4) Mostrar comparação ANTES × DEPOIS e aguardar confirmação (padrão do projeto)
            // ------------------------------------------------------------
            var dataAntes = reserva.DataReserva.Date;
            var dataDepois = novaDataCalculada ?? dataAntes;
            var horaAntes = reserva.HoraInicio.ToString(@"hh\:mm");
            var horaDepois = (novoHorarioParsed ?? reserva.HoraInicio).ToString(@"hh\:mm");
            var qtdAntes = reserva?.QtdPessoas;
            var qtdDepois = novaQtdAbsoluta ?? qtdAntes;

            var resumo = BuildMsgConfirmacaoAlteracao(
                reserva.Id, dataAntes, dataDepois, horaAntes, horaDepois, qtdAntes, qtdDepois);

            // salvar contexto p/ confirmação
            await _conversationRepository.SalvarContextoAsync(args.IdConversa, new ConversationContext
            {
                Estado = "aguardando_confirmacao_alteracao",
                ReservaIdPendente = reserva.Id,
                DadosColetados = new Dictionary<string, object>
        {
            { "reserva_id", reserva.Id },
            { "nova_data", dataDepois.ToString("yyyy-MM-dd") }, // ⬅️ salvar a data calculada
            { "novo_horario", horaDepois },
            { "nova_qtd", qtdDepois }
        },
                ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
            });

            return BuildJsonReply(resumo);
        }

        // Helper local — mesma lógica usada no Interceptor (âncora = data da reserva)
        private static DateTime? ResolverDataComAncora(string texto, DateTime ancora, DateTime hojeSP)
        {
            var norm = texto.ToLower().Normalize(NormalizationForm.FormD);
            norm = new string(norm.Where(ch => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
            norm = norm.Replace("-feira", "").Trim();

            // hoje/amanhã
            if (norm.Contains("hoje")) return hojeSP;
            if (norm.Contains("amanha")) return hojeSP.AddDays(1);

            // dd/MM ou dd/MM/yyyy
            if (DateTime.TryParseExact(norm, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1)) return d1.Date;
            if (DateTime.TryParseExact(norm, "dd/MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2))
            {
                var comp = new DateTime(ancora.Year, d2.Month, d2.Day);
                if (comp <= ancora) comp = comp.AddYears(1);
                return comp.Date;
            }

            // "dia 12"
            var m = System.Text.RegularExpressions.Regex.Match(norm, @"dia\s*(\d{1,2})");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var dia))
            {
                var tentativa = new DateTime(ancora.Year, ancora.Month, dia);
                if (tentativa <= ancora) tentativa = tentativa.AddMonths(1);
                return tentativa.Date;
            }

            // dia da semana
            var dias = new Dictionary<string, DayOfWeek> {
        {"domingo", DayOfWeek.Sunday},{"segunda", DayOfWeek.Monday},{"terca", DayOfWeek.Tuesday},
        {"terça", DayOfWeek.Tuesday},{"quarta", DayOfWeek.Wednesday},{"quinta", DayOfWeek.Thursday},
        {"sexta", DayOfWeek.Friday},{"sabado", DayOfWeek.Saturday},{"sábado", DayOfWeek.Saturday}
    };
            foreach (var kv in dias)
            {
                if (norm.Contains(kv.Key))
                {
                    var delta = ((int)kv.Value - (int)ancora.DayOfWeek + 7) % 7;
                    if (delta == 0) delta = 7; // próxima ocorrência
                    return ancora.AddDays(delta).Date;
                }
            }

            // fallback
            if (DateTime.TryParse(texto, new CultureInfo("pt-BR"), DateTimeStyles.None, out var livre)) return livre.Date;
            return null;
        }

        private string BuildMsgConfirmacaoAlteracao(
    long codigoReserva,
    DateTime dataAntes,
    DateTime dataDepois,
    string horaAntes,
    string horaDepois,
    int? qtdAntes,
    int? qtdDepois)
        {
            var ptbr = new CultureInfo("pt-BR");
            var sb = new StringBuilder();
            sb.AppendLine($"📋 Reserva #{codigoReserva} - Confirme as alterações:");
            sb.AppendLine();

            sb.AppendLine("📅 DATA:");
            if (dataDepois.Date != dataAntes.Date)
            {
                sb.AppendLine($"❌ Antes: {dataAntes:dd/MM/yyyy} ({dataAntes.ToString("dddd", ptbr)})");
                sb.AppendLine($"✅ Depois: {dataDepois:dd/MM/yyyy} ({dataDepois.ToString("dddd", ptbr)})");
            }
            else
            {
                sb.AppendLine($"✔️ Mantém: {dataAntes:dd/MM/yyyy} ({dataAntes.ToString("dddd", ptbr)})");
            }
            sb.AppendLine();

            sb.AppendLine("⏰ HORÁRIO:");
            if (horaDepois == horaAntes)
            {
                sb.AppendLine($"✔️ Mantém: {horaAntes}");
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
                sb.AppendLine($"✔️ Mantém: {qtdAntes}");
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

        private static long? ExtractReservaCode(string mensagem)
        {
            if (string.IsNullOrWhiteSpace(mensagem))
                return null;

            var match = System.Text.RegularExpressions.Regex.Match(
                mensagem,
                @"#(\d+)|c[oó]digo\s*(\d+)|reserva\s*(\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );

            if (match.Success)
            {
                var grupo = match.Groups.Cast<System.Text.RegularExpressions.Group>()
                    .Skip(1)
                    .FirstOrDefault(g => g.Success);

                if (long.TryParse(grupo?.Value, out var codigo))
                    return codigo;
            }

            return null;
        }

        private (int? QtdPessoas, bool EhRelativa, string? Horario) ExtrairMudancasDoTexto(string texto)
        {
            if (string.IsNullOrWhiteSpace(texto))
                return (null, false, null);

            var textoNorm = texto.ToLowerInvariant().Trim();
            int? qtdPessoas = null;
            bool ehRelativa = false;
            string? horario = null;

            // Detectar QUANTIDADE - Relativa (adicionar/tirar)
            var matchAdicionar = System.Text.RegularExpressions.Regex.Match(
                textoNorm,
                @"(?:adicionar|add|mais|incluir)\s+(\d+)\s*(?:pessoa|pessoas)?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (matchAdicionar.Success)
            {
                qtdPessoas = int.Parse(matchAdicionar.Groups[1].Value);
                ehRelativa = true;
            }

            var matchTirar = System.Text.RegularExpressions.Regex.Match(
                textoNorm,
                @"(?:tirar|remover|menos|reduzir)\s+(\d+)\s*(?:pessoa|pessoas)?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (matchTirar.Success)
            {
                qtdPessoas = -int.Parse(matchTirar.Groups[1].Value);
                ehRelativa = true;
            }

            // Detectar QUANTIDADE - Absoluta
            if (!qtdPessoas.HasValue)
            {
                var matchAbsoluta = System.Text.RegularExpressions.Regex.Match(
                    textoNorm,
                    @"(?:para|pra|serão?|serem?|total de)?\s*(\d+)\s*(?:pessoa|pessoas)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (matchAbsoluta.Success)
                {
                    qtdPessoas = int.Parse(matchAbsoluta.Groups[1].Value);
                    ehRelativa = false;
                }
            }

            // Detectar HORÁRIO
            // Formato: 20h, 20:00, 8pm, vinte horas
            var matchHorario = System.Text.RegularExpressions.Regex.Match(
                textoNorm,
                @"(?:às|as|para|pra|horário|horario)?\s*(\d{1,2})(?::(\d{2})|h)?(?:\s*(?:pm|am))?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (matchHorario.Success)
            {
                var hora = int.Parse(matchHorario.Groups[1].Value);
                var minuto = matchHorario.Groups[2].Success ? int.Parse(matchHorario.Groups[2].Value) : 0;

                // Conversão PM/AM
                if (textoNorm.Contains("pm") && hora < 12)
                    hora += 12;
                else if (textoNorm.Contains("am") && hora == 12)
                    hora = 0;

                if (hora >= 0 && hora <= 23 && minuto >= 0 && minuto <= 59)
                {
                    horario = $"{hora:D2}:{minuto:D2}";
                }
            }

            return (qtdPessoas, ehRelativa, horario);
        }

        private string MontarMensagemConfirmacao(
            Reserva reserva,
            Cliente cliente,
            string? novoHorario,
            int? novaQtd,
            bool ehRelativa)
        {
            var qtdAtual = reserva.QtdPessoas ?? 0;
            var horaAtual = reserva.HoraInicio.ToString(@"hh\:mm");
            var nomeCliente = cliente?.Nome ?? "Cliente";

            var msg = new StringBuilder();
            msg.AppendLine($"📋 Reserva #{reserva.Id} - Confirme as alterações:");
            msg.AppendLine();
            msg.AppendLine($"👤 Nome: {nomeCliente}");
            msg.AppendLine($"📅 Data: {reserva.DataReserva:dd/MM/yyyy} ({reserva.DataReserva:dddd})");
            msg.AppendLine();

            // HORÁRIO
            if (!string.IsNullOrWhiteSpace(novoHorario))
            {
                msg.AppendLine("⏰ HORÁRIO:");
                msg.AppendLine($"❌ Antes: {horaAtual}");
                msg.AppendLine($"✅ Depois: {novoHorario}");
            }
            else
            {
                msg.AppendLine("⏰ HORÁRIO:");
                msg.AppendLine($"✔️ Mantém: {horaAtual}");
            }

            msg.AppendLine();

            // QUANTIDADE
            if (novaQtd.HasValue)
            {
                msg.AppendLine("👥 PESSOAS:");
                msg.AppendLine($"❌ Antes: {qtdAtual}");

                if (ehRelativa)
                {
                    var mudanca = novaQtd.Value;
                    var qtdFinal = qtdAtual + mudanca;
                    var sinal = mudanca > 0 ? "+" : "";
                    msg.AppendLine($"✅ Depois: {qtdFinal} ({qtdAtual} {sinal}{mudanca})");
                }
                else
                {
                    msg.AppendLine($"✅ Depois: {novaQtd}");
                }
            }
            else
            {
                msg.AppendLine("👥 PESSOAS:");
                msg.AppendLine($"✔️ Mantém: {qtdAtual}");
            }

            msg.AppendLine();
            msg.Append("Confirma essas mudanças? 😊");

            return msg.ToString();
        }

        private async Task<(Reserva? Reserva, string? MensagemErro)> BuscarReservaInteligente(
            Guid idConversa,
            string? dataTexto,
            long? codigo,
            Guid idCliente,
            Guid idEstabelecimento)
        {
            _logger.LogDebug(
                "[Conversa={Conversa}] BuscarReservaInteligente: dataTexto={Data}, codigo={Codigo}",
                idConversa, dataTexto ?? "null", codigo?.ToString() ?? "null");

            var reservasExistentes = await _reservaRepository.ObterPorClienteEstabelecimentoAsync(idCliente, idEstabelecimento);
            var referenciaAtual = TimeZoneHelper.GetSaoPauloNow();

            var reservasAtivas = reservasExistentes
                .Where(r =>
                {
                    if (r.Status != ReservaStatus.Confirmado) return false;

                    var dataHoraReserva = r.DataReserva.Date.Add(r.HoraInicio);
                    return dataHoraReserva > referenciaAtual;
                })
                .OrderBy(r => r.DataReserva)
                .ThenBy(r => r.HoraInicio)
                .ToList();

            if (!reservasAtivas.Any())
            {
                return (null, "Não encontrei reservas futuras no seu nome.\n\nQuer fazer uma nova reserva? 😊");
            }

            if (codigo.HasValue)
            {
                var porCodigo = reservasAtivas.FirstOrDefault(r => r.Id == codigo.Value);
                if (porCodigo != null)
                    return (porCodigo, null);

                return (null, $"Não encontrei a reserva #{codigo} nas suas reservas futuras.\n\nQuer que eu liste suas reservas? 😊");
            }

            if (!string.IsNullOrWhiteSpace(dataTexto))
            {
                var textoNorm = dataTexto.ToLowerInvariant().Trim();

                if (DateTime.TryParse(dataTexto, new CultureInfo("pt-BR"), System.Globalization.DateTimeStyles.None, out var dataEspecifica))
                {
                    var porData = reservasAtivas.Where(r => r.DataReserva.Date == dataEspecifica.Date).ToList();

                    if (porData.Count == 1)
                        return (porData.First(), null);

                    if (porData.Count > 1)
                    {
                        var lista = new StringBuilder();
                        lista.AppendLine($"Encontrei {porData.Count} reservas para {dataEspecifica:dd/MM/yyyy}:");
                        lista.AppendLine();
                        foreach (var r in porData)
                        {
                            lista.AppendLine($"🎫 #{r.Id} - {r.HoraInicio:hh\\:mm} - {r.QtdPessoas} pessoas");
                        }
                        lista.AppendLine();
                        lista.Append("Qual delas? Informe o código (#) 😊");
                        return (null, lista.ToString());
                    }
                }

                var matchDia = System.Text.RegularExpressions.Regex.Match(textoNorm, @"dia\s*(\d{1,2})");
                if (matchDia.Success && int.TryParse(matchDia.Groups[1].Value, out var dia))
                {
                    var porDia = reservasAtivas.Where(r => r.DataReserva.Day == dia).ToList();

                    if (porDia.Count == 1)
                        return (porDia.First(), null);

                    if (porDia.Count > 1)
                    {
                        var lista = new StringBuilder();
                        lista.AppendLine($"Encontrei {porDia.Count} reservas para o dia {dia}:");
                        lista.AppendLine();
                        foreach (var r in porDia)
                        {
                            lista.AppendLine($"🎫 #{r.Id} - {r.DataReserva:dd/MM/yyyy} - {r.HoraInicio:hh\\:mm}");
                        }
                        lista.AppendLine();
                        lista.Append("Qual delas? Informe o código (#) ou a data completa 😊");
                        return (null, lista.ToString());
                    }
                }

                var meses = new Dictionary<string, int>
                {
                    {"janeiro", 1}, {"fevereiro", 2}, {"março", 3}, {"marco", 3},
                    {"abril", 4}, {"maio", 5}, {"junho", 6},
                    {"julho", 7}, {"agosto", 8}, {"setembro", 9},
                    {"outubro", 10}, {"novembro", 11}, {"dezembro", 12}
                };

                foreach (var mes in meses)
                {
                    if (textoNorm.Contains(mes.Key))
                    {
                        var porMes = reservasAtivas.Where(r => r.DataReserva.Month == mes.Value).ToList();

                        if (porMes.Count == 1)
                            return (porMes.First(), null);

                        if (porMes.Count > 1)
                        {
                            var lista = new StringBuilder();
                            lista.AppendLine($"Encontrei {porMes.Count} reservas em {mes.Key}:");
                            lista.AppendLine();
                            foreach (var r in porMes)
                            {
                                lista.AppendLine($"🎫 #{r.Id} - {r.DataReserva:dd/MM/yyyy} ({r.DataReserva:dddd}) - {r.HoraInicio:hh\\:mm}");
                            }
                            lista.AppendLine();
                            lista.Append("Qual delas? Informe o código (#) ou a data 😊");
                            return (null, lista.ToString());
                        }
                    }
                }
            }

            if (reservasAtivas.Count == 1)
            {
                return (reservasAtivas.First(), null);
            }

            var msg = new StringBuilder();
            msg.AppendLine("📋 Você tem múltiplas reservas. Qual delas?");
            msg.AppendLine();
            foreach (var r in reservasAtivas)
            {
                msg.AppendLine($"🎫 #{r.Id} - {r.DataReserva:dd/MM/yyyy} às {r.HoraInicio:hh\\:mm}");
            }
            msg.AppendLine();
            msg.Append("Informe o código (#) ou a data 😊");

            return (null, msg.ToString());
        }

        private async Task<string> HandleListarReservas(Guid idConversa)
        {
            var conversa = await _conversationRepository.ObterPorIdAsync(idConversa);
            if (conversa == null)
            {
                return BuildJsonReply("Não consegui localizar nossa conversa.");
            }

            var idCliente = conversa.IdCliente;
            var idEstabelecimento = conversa.IdEstabelecimento;

            var reservasExistentes = await _reservaRepository.ObterPorClienteEstabelecimentoAsync(idCliente, idEstabelecimento);
            var referenciaAtual = TimeZoneHelper.GetSaoPauloNow();

            // ✨ JÁ ESTAVA OK: Filtra apenas reservas com status=Confirmado
            var reservasAtivas = reservasExistentes
                .Where(r =>
                {
                    if (r.Status != ReservaStatus.Confirmado) return false;
                    var dataHoraReserva = r.DataReserva.Date.Add(r.HoraInicio);
                    return dataHoraReserva > referenciaAtual;
                })
                .OrderBy(r => r.DataReserva)
                .ThenBy(r => r.HoraInicio)
                .ToList();

            // ✨ ADICIONADO: Log para confirmar filtro de status
            _logger.LogInformation(
                "[Conversa={Conversa}] Filtradas {Total} reservas ativas (status=Confirmado, futuras)",
                idConversa,
                reservasAtivas.Count);

            if (!reservasAtivas.Any())
            {
                return BuildJsonReply("Não encontrei reservas ativas no seu nome.\n\nQuer fazer uma nova reserva? 😊");
            }

            if (reservasAtivas.Count == 1)
            {
                var reserva = reservasAtivas.First();
                var cliente = await _clienteRepository.ObterPorIdAsync(reserva.IdCliente);
                var nomeCliente = cliente?.Nome ?? "Cliente";

                _logger.LogInformation(
                    "[Conversa={Conversa}] Cliente tem apenas 1 reserva. Fast-path direto para alteração.",
                    idConversa);

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

                return BuildJsonReply(msg.ToString());
            }

            var mapeamento = new Dictionary<int, long>();
            for (int i = 0; i < reservasAtivas.Count; i++)
            {
                mapeamento[i + 1] = reservasAtivas[i].Id;
            }

            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = "aguardando_escolha_reserva",
                DadosColetados = new Dictionary<string, object>
                {
                    { "mapeamento_reservas", System.Text.Json.JsonSerializer.Serialize(mapeamento) },
                    { "reservas_json", System.Text.Json.JsonSerializer.Serialize(reservasAtivas.Select(r => new {
                        r.Id,
                        r.DataReserva,
                        r.HoraInicio,
                        r.QtdPessoas
                    }).ToList()) }
                },
                ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
            });

            var msgLista = new StringBuilder();
            msgLista.AppendLine($"📋 Você tem {reservasAtivas.Count} reservas ativas:");
            msgLista.AppendLine();

            foreach (var r in reservasAtivas)
            {
                msgLista.AppendLine($"🎫 Reserva #{r.Id}");
                msgLista.AppendLine($"📅 {r.DataReserva:dd/MM/yyyy} ({r.DataReserva:dddd})");
                msgLista.AppendLine($"⏰ {r.HoraInicio:hh\\:mm}");
                msgLista.AppendLine($"👥 {r.QtdPessoas} pessoas");
                msgLista.AppendLine();
            }

            msgLista.AppendLine("━━━━━━━━━━━━━━━━━━━━━━");
            msgLista.AppendLine();
            msgLista.AppendLine("💬 Qual você quer alterar?");
            msgLista.Append("Responda com o código da reserva (ex: #24, 24) ou a data (ex: dia 11, 11/10)");

            return BuildJsonReply(msgLista.ToString());

            msgLista.Append("Qual você quer alterar? Digite o número (1, 2...) 😊");

            return BuildJsonReply(msgLista.ToString());
        }

        public Task<object[]> GetToolsForOpenAI(Guid idConversa)
        {
            var idConversaString = idConversa.ToString();

            var tools = new object[]
            {
                new {
                    type = "function",
                    function = new {
                        name = "listar_reservas",
                        description = "Lista todas as reservas ativas do cliente vinculadas ao seu telefone. Use quando cliente pedir para alterar/cancelar/ver reservas sem especificar qual.",
                        parameters = new {
                            type = "object",
                            properties = new {
                                idConversa = new {
                                    type = "string",
                                    description = "ID único da conversa atual",
                                    @enum = new[] { idConversaString }
                                }
                            },
                            required = new[] { "idConversa" }
                        }
                    }
                },
                new {
                    type = "function",
                    function = new {
                        name = "atualizar_reserva",
                        description = "Atualiza reserva existente. IMPORTANTE: Se cliente informou filtro (código/#123 OU data) E mudança (horário/quantidade) no MESMO texto, passe TODOS os parâmetros juntos. Detecte e extraia: filtros, mudanças absolutas ('8 pessoas') e relativas ('adicionar 3', 'tirar 2').",
                        parameters = new {
                            type = "object",
                            properties = new {
                                idConversa = new {
                                    type = "string",
                                    description = "ID único da conversa atual",
                                    @enum = new[] { idConversaString }
                                },
                                codigoReserva = new {
                                    type = "integer",
                                    description = "Código da reserva. SEMPRE extraia se cliente mencionar número após pergunta sobre 'qual reserva'. Exemplos de input: '#25', 'código 25', 'reserva 25', 'é a 25', 'a 25', '25' (número solto), 'o 25', 'número 25'. CRÍTICO: Se cliente responde pergunta com número, SEMPRE envie aqui."
                                },
                                filtroData = new {
                                    type = "string",
                                    description = "Data/período mencionado pelo cliente para identificar reserva. Exemplos: 'dia 11', '15/10', 'sexta-feira', 'amanhã', 'outubro'"
                                },
                                novoHorario = new {
                                    type = "string",
                                    description = "Novo horário no formato HH:mm se cliente mencionar mudança de horário. Exemplos: '20h' → '20:00', '19:30' → '19:30'"
                                },
                                novaQtdPessoas = new {
                                    type = "integer",
                                    description = "Quantidade de pessoas. Para mudança RELATIVA (adicionar/tirar): envie o número com sinal (+3 ou -2). Para mudança ABSOLUTA: envie o número final (8)"
                                },
                                ehMudancaRelativa = new {
                                    type = "boolean",
                                    description = "true se cliente usou 'adicionar/tirar/mais/menos' (relativa). false se disse número direto '8 pessoas' (absoluta)"
                                }
                            },
                            required = new[] { "idConversa" }
                        }
                    }
                },
                new {
                    type = "function",
                    function = new {
                        name = "confirmar_reserva",
                        description = "Cria UMA NOVA reserva. NÃO use para atualizar reserva existente. Use apenas quando cliente confirmar criação de nova reserva.",
                        parameters = new {
                            type = "object",
                            properties = new {
                                idConversa = new {
                                    type = "string",
                                    description = "ID único da conversa atual",
                                    @enum = new[] { idConversaString }
                                },
                                nomeCompleto = new {
                                    type = "string",
                                    description = "Nome completo do cliente (mínimo 2 palavras)"
                                },
                                qtdPessoas = new {
                                    type = "integer",
                                    description = "Quantidade de pessoas (1-100)"
                                },
                                data = new {
                                    type = "string",
                                    description = "Data no formato que cliente informou (dd/MM/yyyy, dd/MM, ou texto como 'amanhã')"
                                },
                                hora = new {
                                    type = "string",
                                    description = "Horário no formato HH:mm (ex: 19:00)"
                                }
                            },
                            required = new[] { "idConversa", "nomeCompleto", "qtdPessoas", "data", "hora" }
                        }
                    }
                },
                new {
                    type = "function",
                    function = new {
                        name = "cancelar_reserva",
                        description = "Cancela uma reserva. IMPORTANTE: Se cliente mencionar código (#23) ou número, SEMPRE envie em codigoReserva. Se tiver múltiplas reservas sem código, liste primeiro.",
                        parameters = new {
                            type = "object",
                            properties = new {
                                idConversa = new {
                                    type = "string",
                                    description = "ID único da conversa atual",
                                    @enum = new[] { idConversaString }
                                },
                                codigoReserva = new {
                                    type = "integer",
                                    description = "Código da reserva. Extraia de: '#25', 'código 25', 'reserva 25', 'é a 25', 'a 25', '25' (número solto após pergunta), 'o 25', 'número 25'. Se cliente responde com número após você perguntar 'qual reserva', SEMPRE envie aqui."
                                },
                                motivoCliente = new {
                                    type = "string",
                                    description = "Breve motivo do cancelamento"
                                }
                            },
                            required = new[] { "idConversa" }
                        }
                    }
                }
            };

            return Task.FromResult(tools);
        }

        private async Task<string> HandleEscalarParaHumano(EscalarParaHumanoArgs args)
        {
            args.Motivo = args.Motivo?.Trim() ?? string.Empty;
            args.ResumoConversa = args.ResumoConversa?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(args.Motivo) || string.IsNullOrWhiteSpace(args.ResumoConversa))
            {
                _logger.LogWarning("[Conversa={Conversa}] Motivo ou resumo ausentes na solicitação de escalonamento", args.IdConversa);
                return BuildJsonReply("Claro! Antes de chamar o time, pode me contar rapidinho o motivo do atendimento? 😊");
            }

            var contexto = new HandoverContextDto
            {
                Historico = new[] { $"Resumo: {args.ResumoConversa}", $"Motivo: {args.Motivo}" }
            };

            await _conversationRepository.AtualizarEstadoAsync(args.IdConversa, EstadoConversa.EmAtendimento);
            await _handoverService.ProcessarMensagensTelegramAsync(args.IdConversa, null, false, contexto);

            _logger.LogInformation(
                "[Conversa={Conversa}] Conversa escalada para humano. Motivo: {Motivo}",
                args.IdConversa,
                args.Motivo);

            var msg = new StringBuilder();
            msg.AppendLine("Transferindo você para um atendente humano 👤");
            msg.AppendLine();
            msg.Append("Em instantes alguém irá atendê-lo! 😊");

            return BuildJsonReply(msg.ToString());
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) =================