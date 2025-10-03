// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using System.Globalization;
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

    public class ToolExecutorService
    {
        private const string MissingReservationDataMessage = "Para organizar a sua reserva, preciso do nome completo, número de pessoas, data e horário, por favor.";
        private const string AvisoConfirmacaoTexto = "Sua reserva está confirmada 🎉! Só lembrando que em caso de atraso, se houver clientes esperando, sua mesa poderá ser cedida. Vamos te esperar com alegria 🍻.";

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
                    description = "Confirma uma reserva após ter todos os dados e a confirmação explícita do usuário.",
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
                    name = "escalar_para_humano",
                    description = "Transfere a conversa para um atendente humano quando necessário.",
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
                return BuildJsonReply("Não consegui entender o horário informado. Pode enviar no formato HH:mm?");
            }

            var referenciaAtual = TimeZoneHelper.GetSaoPauloNow();
            var dataCalculada = ParseDataRelativa(args.Data, referenciaAtual);

            if (dataCalculada == null)
            {
                _logger.LogWarning("[Conversa={Conversa}] Não foi possível interpretar a data fornecida pela IA: '{Data}'", args.IdConversa, args.Data);
                return BuildJsonReply($"Não consegui entender a data '{args.Data}'. Pode me enviar com dia e mês, por favor?");
            }

            var dataReserva = dataCalculada.Value.Date;
            var dataHoraReserva = DateTime.SpecifyKind(dataReserva, DateTimeKind.Unspecified).Add(horaConvertida);

            if (dataHoraReserva <= referenciaAtual)
            {
                return BuildJsonReply("Para garantir a melhor experiência, as reservas precisam ser feitas para um horário futuro. Pode escolher outro horário?");
            }

            var limiteMaximo = referenciaAtual.Date.AddDays(14);
            if (dataReserva > limiteMaximo)
            {
                return BuildJsonReply("Atendemos reservas com até 14 dias de antecedência. Pode escolher uma data mais próxima?");
            }

            var conversa = await _conversationRepository.ObterPorIdAsync(args.IdConversa);
            if (conversa == null)
            {
                _logger.LogWarning("[Conversa={Conversa}] Conversa não encontrada ao confirmar reserva", args.IdConversa);
                return BuildJsonReply("Não consegui localizar nossa conversa agora. Pode tentar novamente em instantes?");
            }

            if (string.IsNullOrWhiteSpace(conversa.TelefoneCliente))
            {
                _logger.LogWarning("[Conversa={Conversa}] Telefone não encontrado para confirmação de reserva", args.IdConversa);
                return BuildJsonReply("Desculpe, não consegui identificar seu telefone. Pode me chamar de novo para finalizar?");
            }

            var telefone = conversa.TelefoneCliente;
            var idCliente = conversa.IdCliente;
            var idEstabelecimento = conversa.IdEstabelecimento;

            if (idCliente == Guid.Empty || idEstabelecimento == Guid.Empty)
            {
                _logger.LogWarning("[Conversa={Conversa}] Dados de relacionamento ausentes (cliente ou estabelecimento)", args.IdConversa);
                return BuildJsonReply("Tivemos um probleminha ao confirmar. Pode tentar novamente em instantes?");
            }

            var capacidadeDisponivel = await new ReservaService(_reservaRepository).VerificarCapacidadeDiaAsync(idEstabelecimento, dataReserva, args.QtdPessoas);
            if (!capacidadeDisponivel)
            {
                _logger.LogInformation("[Conversa={Conversa}] Conflito de horário detectado na confirmação de reserva", args.IdConversa);
                return BuildJsonReply("Esse horário já está reservado. Que tal escolher outro horário ou data?");
            }

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
                builder.AppendLine($"✅ Reserva #{idReserva} confirmada com sucesso!");
                builder.AppendLine();
                builder.AppendLine($"- **Nome:** {args.NomeCompleto}");
                builder.AppendLine($"- **Telefone:** {telefone}");
                builder.AppendLine($"- **Pessoas:** {args.QtdPessoas}");
                builder.AppendLine($"- **Data:** {dataFormatada}");
                builder.AppendLine($"- **Horário:** {horaFormatada}");
                builder.AppendLine();
                if (!ContemAviso(builder))
                {
                    builder.AppendLine(AvisoConfirmacaoTexto);
                }

                var reply = builder.ToString().TrimEnd();
                return BuildJsonReply(reply, reservaConfirmada: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Conversa={Conversa}] Falha ao salvar reserva");
                return BuildJsonReply("Tivemos um problema temporário ao salvar a reserva. Pode tentar novamente em instantes?");
            }
        }

        private static bool ContemAviso(StringBuilder builder)
        {
            var texto = builder.ToString().ToLowerInvariant();
            return texto.Contains("sua reserva") &&
                   (texto.Contains("atraso") || texto.Contains("mesa poderá ser cedida"));
        }

        private async Task<string> HandleEscalarParaHumano(EscalarParaHumanoArgs args)
        {
            args.Motivo = args.Motivo?.Trim() ?? string.Empty;
            args.ResumoConversa = args.ResumoConversa?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(args.Motivo) || string.IsNullOrWhiteSpace(args.ResumoConversa))
            {
                _logger.LogWarning("[Conversa={Conversa}] Motivo ou resumo ausentes na solicitação de escalonamento", args.IdConversa);
                return BuildJsonReply("Claro! Antes de chamar o time, pode me contar rapidinho o motivo do atendimento?");
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

            return BuildJsonReply("Transferindo você para um atendente humano. Em instantes alguém irá atendê-lo.");
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

            switch (textoNormalizado)
            {
                case "hoje":
                    return hoje;
                case "amanha":
                    return hoje.AddDays(1);
                case "depois de amanha":
                    return hoje.AddDays(2);
            }

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
                    var dataResultado = hoje;
                    while (dataResultado.DayOfWeek != diaAlvo)
                    {
                        dataResultado = dataResultado.AddDays(1);
                    }

                    if (dataResultado == hoje || textoNormalizado.Contains("que vem") || textoNormalizado.Contains("proxima"))
                    {
                        dataResultado = dataResultado.AddDays(7);
                    }

                    return dataResultado;
                }
            }

            if (DateTime.TryParseExact(textoNormalizado, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dataEspecifica))
            {
                return dataEspecifica.Date;
            }

            if (DateTime.TryParse(dataTexto, new CultureInfo("pt-BR"), DateTimeStyles.None, out var dataOutroFormato))
            {
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

