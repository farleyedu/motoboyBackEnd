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

    public class ToolExecutorService
    {
        private const string MissingReservationDataMessage = "Para organizar a sua reserva, preciso de algumas informações:\n\n📋 Nome completo\n👥 Número de pessoas\n📅 Data\n⏰ Horário\n\nPode me passar esses dados? 😊";

        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly ILogger<ToolExecutorService> _logger;
        private readonly IConversationRepository _conversationRepository;
        private readonly HandoverService _handoverService;
        private readonly IReservaRepository _reservaRepository;

        public ToolExecutorService(
            ILogger<ToolExecutorService> logger,
            IConversationRepository conversationRepository,
            HandoverService handoverService,
            IReservaRepository reservaRepository)
        {
            _logger = logger;
            _conversationRepository = conversationRepository;
            _handoverService = handoverService;
            _reservaRepository = reservaRepository;
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

                    case "cancelar_reserva":
                        var cancelarArgs = JsonSerializer.Deserialize<CancelarReservaArgs>(argsJson, JsonOptions);
                        if (cancelarArgs == null)
                        {
                            return BuildJsonReply("Argumentos inválidos para cancelar reserva.");
                        }
                        return await HandleCancelarReserva(cancelarArgs);

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

            if (DadosReservaInvalidos(args))
            {
                _logger.LogWarning("[Conversa={Conversa}] Dados inválidos recebidos na confirmação de reserva: {@Args}", args.IdConversa, args);
                return BuildJsonReply(MissingReservationDataMessage);
            }

            if (!TryParseHora(args.Hora, out var horaConvertida))
            {
                _logger.LogWarning("[Conversa={Conversa}] Horário inválido recebido: {Hora}", args.IdConversa, args.Hora);
                return BuildJsonReply("Não consegui entender o horário informado.\n\nPode enviar no formato HH:mm? 😊\nExemplo: 19:00");
            }

            var referenciaAtual = TimeZoneHelper.GetSaoPauloNow();
            _logger.LogInformation("[Conversa={Conversa}] Referência atual (SP): {Ref}, Tentando parsear: '{Data}'", args.IdConversa, referenciaAtual, args.Data);

            var dataCalculada = ParseDataRelativa(args.Data, referenciaAtual);

            if (dataCalculada == null)
            {
                _logger.LogWarning("[Conversa={Conversa}] Não foi possível interpretar a data fornecida pela IA: '{Data}'", args.IdConversa, args.Data);
                return BuildJsonReply($"Não consegui entender a data '{args.Data}'.\n\nPode me enviar com dia e mês, por favor? 😊\nExemplo: 25/12 ou 25/12/2025");
            }

            var dataReserva = dataCalculada.Value.Date;
            var dataHoraReserva = DateTime.SpecifyKind(dataReserva, DateTimeKind.Unspecified).Add(horaConvertida);

            _logger.LogInformation("[Conversa={Conversa}] Data calculada: {DataReserva}, Horário: {Hora}", args.IdConversa, dataReserva.ToString("dd/MM/yyyy"), horaConvertida);

            if (dataHoraReserva <= referenciaAtual)
            {
                return BuildJsonReply("Para garantir a melhor experiência, as reservas precisam ser feitas para um horário futuro.\n\nPode escolher outro horário? 😊");
            }

            var limiteMaximo = referenciaAtual.Date.AddDays(14);
            if (dataReserva > limiteMaximo)
            {
                return BuildJsonReply("Atendemos reservas com até 14 dias de antecedência.\n\nPode escolher uma data mais próxima? 😊");
            }

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

            if (idCliente == Guid.Empty || idEstabelecimento == Guid.Empty)
            {
                _logger.LogWarning("[Conversa={Conversa}] Dados de relacionamento ausentes (cliente ou estabelecimento)", args.IdConversa);
                return BuildJsonReply("Tivemos um probleminha ao confirmar.\n\nPode tentar novamente em instantes? 😊");
            }

            // Verificação de reservas existentes
            var reservasExistentes = await _reservaRepository.ObterPorClienteEstabelecimentoAsync(idCliente, idEstabelecimento);
            var reservasAtivas = reservasExistentes
                .Where(r => r.Status == ReservaStatus.Confirmado && r.DataReserva >= referenciaAtual.Date)
                .OrderBy(r => r.DataReserva)
                .ToList();

            // Verifica se já tem reserva no MESMO DIA
            var reservaMesmoDia = reservasAtivas.FirstOrDefault(r => r.DataReserva.Date == dataReserva.Date);
            if (reservaMesmoDia != null)
            {
                var horaExistente = reservaMesmoDia.HoraInicio.ToString(@"hh\:mm");
                var dataExistente = reservaMesmoDia.DataReserva.ToString("dd/MM/yyyy");

                _logger.LogInformation("[Conversa={Conversa}] Cliente já possui reserva no mesmo dia: {Data} às {Hora}",
                    args.IdConversa, dataExistente, horaExistente);

                var msgDuplicada = new StringBuilder();
                msgDuplicada.AppendLine("Você já possui uma reserva confirmada para este dia:");
                msgDuplicada.AppendLine();
                msgDuplicada.AppendLine($"📅 Data: {dataExistente}");
                msgDuplicada.AppendLine($"⏰ Horário: {horaExistente}");
                msgDuplicada.AppendLine();
                msgDuplicada.AppendLine("Gostaria de:");
                msgDuplicada.AppendLine("1️⃣ Manter a reserva existente");
                msgDuplicada.AppendLine("2️⃣ Cancelar e criar esta nova");
                msgDuplicada.AppendLine();
                msgDuplicada.Append("Me avisa o que prefere! 😊");

                return BuildJsonReply(msgDuplicada.ToString());
            }

            // Capacidade disponível
            var capacidadeDisponivel = await new ReservaService(_reservaRepository).VerificarCapacidadeDiaAsync(idEstabelecimento, dataReserva, args.QtdPessoas);
            if (!capacidadeDisponivel)
            {
                _logger.LogInformation("[Conversa={Conversa}] Conflito de capacidade detectado na confirmação de reserva", args.IdConversa);
                return BuildJsonReply("Esse horário já está com a capacidade máxima 😔\n\nQue tal escolher outro horário ou data?");
            }

            // Criar reserva
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

            try
            {
                var idReserva = await _reservaRepository.AdicionarAsync(reserva);

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

                _logger.LogInformation(
                    "[Conversa={Conversa}] Reserva #{ReservaId} confirmada: {Nome}, {Qtd} pessoas, {Data} às {Hora}",
                    args.IdConversa,
                    idReserva,
                    args.NomeCompleto,
                    args.QtdPessoas,
                    dataFormatada,
                    horaFormatada);

                var builder = new StringBuilder();
                builder.AppendLine("🎉 Sua reserva está confirmadíssima! 🎉");
                builder.AppendLine();
                builder.AppendLine($"Oi, {args.NomeCompleto}! Já estamos preparando um lugar especial para você e seus convidados.");
                builder.AppendLine();
                builder.AppendLine("Confira os dados do seu agendamento:");
                builder.AppendLine();
                builder.AppendLine($"📅 Data: {dataFormatada}");
                builder.AppendLine($"⏰ Horário: {horaFormatada}");
                builder.AppendLine($"👥 Pessoas: {args.QtdPessoas}");
                builder.AppendLine();
                builder.AppendLine($"🎫 Seu código de reserva é o #{idReserva}.");
                builder.AppendLine("Caso precise alterar ou cancelar, é só nos informar este número para agilizar o atendimento!");
                builder.AppendLine();
                builder.AppendLine("⚠️ Atenção: Para que todos tenham uma ótima experiência, sua mesa ficará reservada por até 15 minutos após o horário marcado. Agradecemos a compreensão!");
                builder.AppendLine();
                builder.Append("Será um prazer receber vocês! ✨🥂");

                var reply = builder.ToString();
                return BuildJsonReply(reply, reservaConfirmada: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Conversa={Conversa}] Falha ao salvar reserva", args.IdConversa);
                return BuildJsonReply("Tivemos um problema temporário ao salvar a reserva.\n\nPode tentar novamente em instantes? 😊");
            }
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

            // Se tiver múltiplas, pede para o cliente especificar
            var listaReservas = new StringBuilder();
            listaReservas.AppendLine("Você tem mais de uma reserva ativa:");
            listaReservas.AppendLine();
            foreach (var r in reservasAtivas)
            {
                listaReservas.AppendLine($"📅 {r.DataReserva:dd/MM/yyyy} às {r.HoraInicio:hh\\:mm} - {r.QtdPessoas} pessoas");
            }
            listaReservas.AppendLine();
            listaReservas.Append("Qual delas você gostaria de cancelar? Me informe a data 😊");

            return BuildJsonReply(listaReservas.ToString());
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

        private static bool DadosReservaInvalidos(ConfirmarReservaArgs args)
        {
            if (string.IsNullOrWhiteSpace(args.NomeCompleto) || HasMissingValueIndicator(args.NomeCompleto))
            {
                return true;
            }

            if (args.QtdPessoas <= 0)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(args.Data) || HasMissingValueIndicator(args.Data))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(args.Hora) || HasMissingValueIndicator(args.Hora))
            {
                return true;
            }

            return false;
        }

        private static bool HasMissingValueIndicator(string valor)
        {
            var normalized = valor.Trim().ToLowerInvariant();

            if (normalized.Length == 0)
            {
                return true;
            }

            if (normalized == "string" || normalized == "null" || normalized == "undefined")
            {
                return true;
            }

            if (normalized.Contains("nao inform") || normalized.Contains("a definir") || normalized.Contains("a combinar"))
            {
                return true;
            }

            if (normalized.Contains("informada") && normalized.Contains("ainda"))
            {
                return true;
            }

            return false;
        }

        private static bool TryParseHora(string horaTexto, out TimeSpan hora)
        {
            return TimeSpan.TryParseExact(horaTexto, @"hh\:mm", CultureInfo.InvariantCulture, out hora);
        }

        private DateTime? ParseDataRelativa(string dataTexto, DateTime referenciaAtual)
        {
            if (string.IsNullOrWhiteSpace(dataTexto))
            {
                return null;
            }

            var textoNormalizado = RemoveDiacritics(dataTexto.ToLowerInvariant().Trim()).Replace("-feira", string.Empty);
            var hoje = referenciaAtual.Date;

            _logger.LogDebug("ParseDataRelativa: texto='{Texto}', normalizado='{Norm}', hoje={Hoje}", dataTexto, textoNormalizado, hoje);

            // Termos relativos exatos
            if (textoNormalizado == "hoje")
            {
                _logger.LogDebug("Detectado: HOJE -> {Data}", hoje);
                return hoje;
            }

            if (textoNormalizado == "amanha")
            {
                var resultado = hoje.AddDays(1);
                _logger.LogDebug("Detectado: AMANHÃ -> {Data}", resultado);
                return resultado;
            }

            if (textoNormalizado == "depois de amanha")
            {
                var resultado = hoje.AddDays(2);
                _logger.LogDebug("Detectado: DEPOIS DE AMANHÃ -> {Data}", resultado);
                return resultado;
            }

            // Dias da semana
            var diasDaSemana = new Dictionary<string, DayOfWeek>
            {
                { "domingo", DayOfWeek.Sunday },
                { "segunda", DayOfWeek.Monday },
                { "terca", DayOfWeek.Tuesday },
                { "quarta", DayOfWeek.Wednesday },
                { "quinta", DayOfWeek.Thursday },
                { "sexta", DayOfWeek.Friday },
                { "sabado", DayOfWeek.Saturday }
            };

            foreach (var dia in diasDaSemana)
            {
                if (textoNormalizado.Contains(dia.Key))
                {
                    var diaAlvo = dia.Value;
                    var dataResultado = hoje.AddDays(1);

                    while (dataResultado.DayOfWeek != diaAlvo)
                    {
                        dataResultado = dataResultado.AddDays(1);
                    }

                    if (textoNormalizado.Contains("que vem") || textoNormalizado.Contains("proxima"))
                    {
                        dataResultado = dataResultado.AddDays(7);
                    }

                    _logger.LogDebug("Detectado dia da semana: {Dia} -> {Data}", dia.Key, dataResultado);
                    return dataResultado;
                }
            }

            // Formatos de data específicos
            if (DateTime.TryParseExact(textoNormalizado, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dataEspecifica))
            {
                _logger.LogDebug("Parse formato dd/MM/yyyy: {Data}", dataEspecifica);
                return dataEspecifica.Date;
            }

            if (DateTime.TryParse(dataTexto, new CultureInfo("pt-BR"), DateTimeStyles.None, out var dataOutroFormato))
            {
                _logger.LogDebug("Parse genérico pt-BR: {Data}", dataOutroFormato);
                return dataOutroFormato.Date;
            }

            _logger.LogWarning("Não foi possível fazer o parse da data relativa: '{DataTexto}'", dataTexto);
            return null;
        }

        private static string RemoveDiacritics(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (category != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) =================