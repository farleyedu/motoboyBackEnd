// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
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
        public string MotivoCliente { get; set; } = string.Empty;
    }

    public class AtualizarReservaArgs
    {
        public Guid IdConversa { get; set; }
        public long? CodigoReserva { get; set; }
        public string? NovoHorario { get; set; }
        public int? NovaQtdPessoas { get; set; }
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
                    description = "Lista todas as reservas ativas do cliente. Use quando ele pedir para alterar/ver/cancelar sem especificar qual.",
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
                    description = "Cancela uma reserva existente do cliente. Só executar após confirmação explícita do cliente.",
                    parameters = new {
                        type = "object",
                        properties = new {
                            idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                            motivoCliente = new { type = "string", description = "Breve motivo do cancelamento informado pelo cliente" }
                        },
                        required = new[] { "idConversa", "motivoCliente" }
                    }
                },
                new {
                    type = "function",
                    name = "atualizar_reserva",
                    description = "Atualiza uma reserva existente. Use quando cliente mencionar código (#123) ou quiser alterar horário/quantidade.",
                    parameters = new {
                        type = "object",
                        properties = new {
                            idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                            codigoReserva = new { type = "integer", description = "Código da reserva (#123). Obrigatório se cliente mencionar." },
                            novoHorario = new { type = "string", description = "Novo horário HH:mm (opcional)" },
                            novaQtdPessoas = new { type = "integer", description = "Nova quantidade de pessoas (opcional)" }
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

            var reservasAtivas = reservasExistentes
                .Where(r => r.Status == ReservaStatus.Confirmado && r.DataReserva >= referenciaAtual.Date)
                .OrderBy(r => r.DataReserva)
                .ToList();

            if (!reservasAtivas.Any())
            {
                _logger.LogInformation("[Conversa={Conversa}] Cliente tentou cancelar mas não possui reservas ativas", args.IdConversa);
                return BuildJsonReply("Não encontrei nenhuma reserva ativa no seu nome 🤔\n\nSe precisar de ajuda, é só me avisar! 😊");
            }

            // Se tiver apenas 1 reserva, cancela direto
            if (reservasAtivas.Count == 1)
            {
                var reserva = reservasAtivas.First();
                await _reservaRepository.CancelarReservaAsync(reserva.Id);

                var dataFormatada = reserva.DataReserva.ToString("dd/MM/yyyy");
                var horaFormatada = reserva.HoraInicio.ToString(@"hh\:mm");

                _logger.LogInformation(
                    "[Conversa={Conversa}] Reserva #{IdReserva} cancelada. Motivo: {Motivo}",
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

            // Se tiver múltiplas, listar com CÓDIGO
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
            {
                return BuildJsonReply("Não consegui localizar nossa conversa.\n\nPode tentar novamente? 😊");
            }

            var idCliente = conversa.IdCliente;
            var idEstabelecimento = conversa.IdEstabelecimento;

            // Usar busca inteligente
            var (reserva, mensagemErro) = await BuscarReservaInteligente(
                args.IdConversa,
                null, // dataTexto será inferido do contexto se necessário
                args.CodigoReserva,
                idCliente,
                idEstabelecimento);

            if (reserva == null)
            {
                return BuildJsonReply(mensagemErro ?? "Não consegui identificar qual reserva alterar.");
            }

            // 1. Verificar se reserva já passou
            var referenciaAtual = TimeZoneHelper.GetSaoPauloNow();
            var dataHoraReserva = reserva.DataReserva.Date.Add(reserva.HoraInicio);

            if (dataHoraReserva <= referenciaAtual)
            {
                return BuildJsonReply($"⏰ A reserva #{reserva.Id} já foi finalizada.\n\n" +
                                     $"📅 Era para: {reserva.DataReserva:dd/MM/yyyy} às {reserva.HoraInicio:hh\\:mm}\n\n" +
                                     "Não é possível alterar reservas passadas. Quer fazer uma nova? 😊");
            }

            // 2. Verificar permissão (mesmo telefone OU tem código)
            if (!args.CodigoReserva.HasValue && reserva.IdCliente != idCliente)
            {
                return BuildJsonReply("Não encontrei essa reserva no seu telefone. 😕\n\n" +
                                     "💡 Para alterar reserva de outra pessoa, você precisa " +
                                     "informar o código (#123).");
            }

            // 3. Validar novo horário (se informado)
            if (!string.IsNullOrWhiteSpace(args.NovoHorario))
            {
                if (!TimeSpan.TryParseExact(args.NovoHorario, @"hh\:mm",
                    System.Globalization.CultureInfo.InvariantCulture, out var horarioParseado))
                {
                    return BuildJsonReply("Formato de horário inválido. Use HH:MM (ex: 19:00)");
                }

                // Validar horário de funcionamento
                var diaSemana = reserva.DataReserva.DayOfWeek;
                TimeSpan horaAbertura, horaFechamento;

                if (diaSemana >= DayOfWeek.Monday && diaSemana <= DayOfWeek.Friday)
                {
                    horaAbertura = new TimeSpan(17, 0, 0);
                    horaFechamento = new TimeSpan(23, 59, 59);
                }
                else if (diaSemana == DayOfWeek.Saturday)
                {
                    horaAbertura = new TimeSpan(12, 0, 0);
                    horaFechamento = new TimeSpan(23, 59, 59);
                }
                else
                {
                    horaAbertura = new TimeSpan(12, 0, 0);
                    horaFechamento = new TimeSpan(23, 59, 59);
                }

                if (horarioParseado < horaAbertura || horarioParseado > horaFechamento)
                {
                    var diaDesc = diaSemana == DayOfWeek.Saturday ? "sábado" :
                                  diaSemana == DayOfWeek.Sunday ? "domingo" : "segunda a sexta";

                    return BuildJsonReply($"⏰ Horário inválido para {diaDesc}.\n\n" +
                                         "🕐 Horários:\n" +
                                         "• Seg-Sex: 17h às 00h30\n" +
                                         "• Sábado: 12h à 01h\n" +
                                         "• Domingo: 12h às 00h30");
                }
            }

            // 4. Validar nova quantidade (se informada) - REGRA 50/110
            if (args.NovaQtdPessoas.HasValue)
            {
                var novaQtd = args.NovaQtdPessoas.Value;

                if (novaQtd <= 0)
                {
                    return BuildJsonReply("A quantidade precisa ser maior que zero. 😊");
                }

                if (novaQtd > 100)
                {
                    return BuildJsonReply("Para grupos acima de 100 pessoas, entre em contato conosco. 📞");
                }

                // ✨ VALIDAR CAPACIDADE: 50 (mesmo dia) ou 110 (dias futuros)
                var hoje = referenciaAtual.Date;
                var ehMesmoDia = reserva.DataReserva.Date == hoje;
                var capacidadeMaxima = ehMesmoDia ? 50 : 110;

                // Buscar todas as reservas do dia
                var reservasDia = await _reservaRepository.ObterPorEstabelecimentoDataAsync(
                    idEstabelecimento, reserva.DataReserva);

                // Calcular capacidade ocupada (excluindo a reserva atual)
                var capacidadeOcupada = reservasDia
                    .Where(r => r.Id != reserva.Id && r.Status == ReservaStatus.Confirmado)
                    .Sum(r => r.QtdPessoas ?? 0);

                var vagasDisponiveis = capacidadeMaxima - capacidadeOcupada;

                if (novaQtd > vagasDisponiveis)
                {
                    var tipoReserva = ehMesmoDia ? "hoje" : "este dia";
                    var erroCapacidade = new StringBuilder();
                    erroCapacidade.AppendLine($"😔 Não conseguimos aumentar para {novaQtd} pessoas.");
                    erroCapacidade.AppendLine();
                    erroCapacidade.AppendLine($"📊 Situação de {reserva.DataReserva:dd/MM/yyyy}:");
                    erroCapacidade.AppendLine($"• Tipo: Reserva {tipoReserva}");
                    erroCapacidade.AppendLine($"• Capacidade máxima: {capacidadeMaxima} pessoas");
                    erroCapacidade.AppendLine($"• Já reservadas: {capacidadeOcupada} pessoas");
                    erroCapacidade.AppendLine($"• Disponíveis: {vagasDisponiveis} pessoas");
                    erroCapacidade.AppendLine();
                    erroCapacidade.AppendLine("💡 Você pode:");
                    erroCapacidade.AppendLine($"• Reduzir para até {vagasDisponiveis} pessoas");
                    erroCapacidade.AppendLine("• Escolher outro dia");
                    erroCapacidade.AppendLine();
                    erroCapacidade.Append("Como prefere continuar? 😊");

                    return BuildJsonReply(erroCapacidade.ToString());
                }
            }

            // Resto do método continua igual...
            bool houveAlteracao = false;

            if (!string.IsNullOrWhiteSpace(args.NovoHorario) && TimeSpan.TryParseExact(args.NovoHorario, @"hh\:mm", CultureInfo.InvariantCulture, out var novoHorario))
            {
                reserva.HoraInicio = novoHorario;
                houveAlteracao = true;
            }

            if (args.NovaQtdPessoas.HasValue && args.NovaQtdPessoas.Value > 0)
            {
                reserva.QtdPessoas = args.NovaQtdPessoas.Value;
                houveAlteracao = true;
            }

            if (!houveAlteracao)
            {
                return BuildJsonReply("Não identifiquei o que você quer atualizar.\n\nPode me dizer o novo horário ou quantidade de pessoas? 😊");
            }

            reserva.DataAtualizacao = DateTime.UtcNow;
            await _reservaRepository.AtualizarAsync(reserva);

            _logger.LogInformation(
                "[Conversa={Conversa}] Reserva #{Codigo} atualizada com sucesso",
                args.IdConversa,
                reserva.Id);

            var msg = new StringBuilder();
            msg.AppendLine("✅ Reserva atualizada com sucesso! 🎉");
            msg.AppendLine();
            msg.AppendLine($"🎫 Código: #{reserva.Id}");
            msg.AppendLine($"📅 Data: {reserva.DataReserva:dd/MM/yyyy}");
            msg.AppendLine($"⏰ Horário: {reserva.HoraInicio:hh\\:mm}");
            msg.AppendLine($"👥 Pessoas: {reserva.QtdPessoas}");
            msg.AppendLine();
            msg.Append("Nos vemos lá! ✨🥂");

            return BuildJsonReply(msg.ToString());
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
        private async Task<(Reserva? Reserva, string? MensagemErro)> BuscarReservaInteligente(
            Guid idConversa,
            string? dataTexto,
            long? codigo,
            Guid idCliente,
            Guid idEstabelecimento)
        {
            // Log para debugging
            _logger.LogDebug(
                "[Conversa={Conversa}] BuscarReservaInteligente: dataTexto={Data}, codigo={Codigo}",
                idConversa, dataTexto ?? "null", codigo?.ToString() ?? "null");

            var reservasExistentes = await _reservaRepository.ObterPorClienteEstabelecimentoAsync(idCliente, idEstabelecimento);
            var referenciaAtual = TimeZoneHelper.GetSaoPauloNow();

            var reservasAtivas = reservasExistentes
                .Where(r => {
                    if (r.Status != ReservaStatus.Confirmado) return false;

                    // Data+hora precisa ser futura
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

            // Busca por código (prioridade máxima)
            if (codigo.HasValue)
            {
                var porCodigo = reservasAtivas.FirstOrDefault(r => r.Id == codigo.Value);
                if (porCodigo != null)
                    return (porCodigo, null);

                return (null, $"Não encontrei a reserva #{codigo} nas suas reservas futuras.\n\nQuer que eu liste suas reservas? 😊");
            }

            // Busca por data/contexto
            if (!string.IsNullOrWhiteSpace(dataTexto))
            {
                var textoNorm = dataTexto.ToLowerInvariant().Trim();

                // Tentar parsear data específica
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

                // Buscar por dia do mês (ex: "dia 7", "dia 15")
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

                // Buscar por mês (ex: "junho", "outubro")
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

            // Se só tem uma reserva, retornar ela
            if (reservasAtivas.Count == 1)
            {
                return (reservasAtivas.First(), null);
            }

            // Múltiplas reservas sem contexto suficiente
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

            var reservasAtivas = reservasExistentes
                .Where(r => {
                    if (r.Status != ReservaStatus.Confirmado) return false;
                    var dataHoraReserva = r.DataReserva.Date.Add(r.HoraInicio);
                    return dataHoraReserva > referenciaAtual;
                })
                .OrderBy(r => r.DataReserva)
                .ThenBy(r => r.HoraInicio)
                .ToList();

            if (!reservasAtivas.Any())
            {
                return BuildJsonReply("Não encontrei reservas ativas no seu nome.\n\nQuer fazer uma nova reserva? 😊");
            }

            // ✨ FAST-PATH: Se só tem 1 reserva, vai direto para coleta de dados
            if (reservasAtivas.Count == 1)
            {
                var reserva = reservasAtivas.First();
                var cliente = await _clienteRepository.ObterPorIdAsync(reserva.IdCliente);
                var nomeCliente = cliente?.Nome ?? "Cliente";

                _logger.LogInformation(
                    "[Conversa={Conversa}] Cliente tem apenas 1 reserva. Fast-path direto para alteração.",
                    idConversa);

                // Salvar contexto para aguardar dados de alteração
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

            // ✨ Se tem múltiplas reservas, aí sim precisa listar
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
            msgLista.AppendLine("📋 Encontrei estas reservas ativas:");
            msgLista.AppendLine();

            int numero = 1;
            foreach (var r in reservasAtivas)
            {
                var emoji = numero == 1 ? "1️⃣" : numero == 2 ? "2️⃣" : numero == 3 ? "3️⃣" : $"{numero}️⃣";
                msgLista.AppendLine($"{emoji} Reserva #{r.Id}");
                msgLista.AppendLine($"📅 Data: {r.DataReserva:dd/MM/yyyy} ({r.DataReserva:dddd})");
                msgLista.AppendLine($"⏰ Horário: {r.HoraInicio:hh\\:mm}");
                msgLista.AppendLine($"👥 Pessoas: {r.QtdPessoas}");
                msgLista.AppendLine();
                numero++;
            }

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
                        description = "Atualiza uma reserva existente. Use após listar reservas e cliente escolher qual atualizar. Com código (#123) pode alterar qualquer reserva. Sem código, só altera se mesmo telefone.",
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
                                    description = "Código da reserva (#123). Obrigatório se cliente mencionar."
                                },
                                novoHorario = new {
                                    type = "string",
                                    description = "Novo horário no formato HH:mm (ex: 20:00). Opcional."
                                },
                                novaQtdPessoas = new {
                                    type = "integer",
                                    description = "Nova quantidade de pessoas. Opcional."
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
                        description = "Cancela uma reserva. Se cliente tem múltiplas, use listar_reservas primeiro.",
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
                                    description = "Código da reserva a cancelar. Opcional se cliente tem apenas uma."
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