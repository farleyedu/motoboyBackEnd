// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;

namespace APIBack.Automation.Services
{
    // DTOs para deserialização segura dos argumentos das ferramentas
    public class ConfirmarReservaArgs
    {
        public Guid IdConversa { get; set; }
        public string NomeCompleto { get; set; }
        public int QtdPessoas { get; set; }
        public string Data { get; set; }
        public string Hora { get; set; }
    }

    public class EscalarParaHumanoArgs
    {
        public Guid IdConversa { get; set; }
        public string Motivo { get; set; }
        public string ResumoConversa { get; set; }
    }

    public class ToolExecutorService
    {
        private readonly ILogger<ToolExecutorService> _logger;
        private readonly IConversationRepository _conversationRepository;
        private readonly HandoverService _handoverService;
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        public ToolExecutorService(
            ILogger<ToolExecutorService> logger,
            IConversationRepository conversationRepository,
            HandoverService handoverService)
        {
            _logger = logger;
            _conversationRepository = conversationRepository;
            _handoverService = handoverService;
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
                            data = new { type = "string", description = "A data da reserva, exatamente como o usuário informou (ex: 'amanhã', 'sexta que vem', '25/12/2025'). NÃO calcule ou formate a data." },
                            hora = new { type = "string", description = "Horário da reserva no formato HH:mm" }
                        },
                        required = new[] { "idConversa", "nomeCompleto", "qtdPessoas", "data", "hora" }
                    }
                },
                new {
                    type = "function",
                    name = "escalar_para_humano",
                    description = "Transfere a conversa para um atendente humano quando solicitado pelo cliente ou quando o bot não consegue resolver.",
                    parameters = new {
                        type = "object",
                        properties = new {
                            idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                            motivo = new { type = "string", description = "Breve explicação do motivo do escalonamento para contexto do atendente humano" },
                            resumoConversa = new { type = "string", description = "Resumo breve (2-3 frases) do que foi discutido até agora na conversa" }
                        },
                        required = new[] { "idConversa", "motivo", "resumoConversa" }
                    }
                }
            };
        }

        private string BuildJsonReply(string reply)
        {
            var obj = new { reply };
            return JsonSerializer.Serialize(obj, JsonOptions);
        }

        public async Task<string> ExecuteToolAsync(string toolName, string argsJson)
        {
            try
            {
                // ================= CORREÇÃO APLICADA AQUI =================
                // Desembrulha o JSON se ele vier como uma string escapada
                if (argsJson.StartsWith("\"") && argsJson.EndsWith("\""))
                {
                    argsJson = JsonSerializer.Deserialize<string>(argsJson) ?? string.Empty;
                }
                // ================= FIM DA CORREÇÃO =================

                switch (toolName)
                {
                    case "confirmar_reserva":
                        var reservaArgs = JsonSerializer.Deserialize<ConfirmarReservaArgs>(argsJson, JsonOptions);
                        if (reservaArgs == null) return BuildJsonReply("Argumentos inválidos para confirmar reserva.");
                        return await HandleConfirmarReserva(reservaArgs);

                    case "escalar_para_humano":
                        var escalarArgs = JsonSerializer.Deserialize<EscalarParaHumanoArgs>(argsJson, JsonOptions);
                        if (escalarArgs == null) return BuildJsonReply("Argumentos inválidos para escalar ao atendimento.");
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
            DateTime? dataCalculada = ParseDataRelativa(args.Data);

            if (dataCalculada == null)
            {
                _logger.LogWarning("Não foi possível interpretar a data fornecida pela IA: '{Data}'", args.Data);
                return BuildJsonReply($"Não consegui entender a data '{args.Data}'. Por favor, poderia especificar a data novamente usando dia e mês?");
            }

            var dataFormatada = dataCalculada.Value.ToString("dd/MM/yyyy");

            var detalhesReserva = new HandoverContextDto
            {
                ClienteNome = args.NomeCompleto,
                NumeroPessoas = args.QtdPessoas.ToString(),
                Dia = dataFormatada,
                Horario = args.Hora
            };

            await _conversationRepository.AtualizarEstadoAsync(args.IdConversa, EstadoConversa.FechadoAutomaticamente);
            await _handoverService.ProcessarMensagensTelegramAsync(args.IdConversa, null, true, detalhesReserva);

            _logger.LogInformation(
                "[Conversa={Conversa}] Reserva confirmada: {Nome}, {Qtd} pessoas, {Data} às {Hora}",
                args.IdConversa, args.NomeCompleto, args.QtdPessoas, dataFormatada, args.Hora
            );

            var replyMessage = $"✅ Reserva confirmada com sucesso!\n\n- **Nome:** {args.NomeCompleto}\n- **Pessoas:** {args.QtdPessoas}\n- **Data:** {dataFormatada}\n- **Horário:** {args.Hora}\n\nAté breve! 🌸✨";
            return BuildJsonReply(replyMessage);
        }

        private async Task<string> HandleEscalarParaHumano(EscalarParaHumanoArgs args)
        {
            var contexto = new HandoverContextDto
            {
                Historico = new[] { $"Resumo: {args.ResumoConversa}", $"Motivo: {args.Motivo}" }
            };

            await _conversationRepository.AtualizarEstadoAsync(args.IdConversa, EstadoConversa.EmAtendimento);
            await _handoverService.ProcessarMensagensTelegramAsync(args.IdConversa, null, false, contexto);

            _logger.LogInformation(
                "[Conversa={Conversa}] Conversa escalada para humano. Motivo: {Motivo}",
                args.IdConversa, args.Motivo
            );

            return BuildJsonReply("Transferindo você para um atendente humano. Em instantes alguém irá atendê-lo.");
        }

        private DateTime? ParseDataRelativa(string dataTexto)
        {
            if (string.IsNullOrWhiteSpace(dataTexto)) return null;

            dataTexto = dataTexto.ToLower().Trim().Replace("-feira", "");
            var hoje = DateTime.Now.Date; // Use a data atual do servidor

            switch (dataTexto)
            {
                case "hoje": return hoje;
                case "amanhã": return hoje.AddDays(1);
                case "depois de amanhã": return hoje.AddDays(2);
            }

            var diasDaSemana = new Dictionary<string, DayOfWeek>
            {
                {"domingo", DayOfWeek.Sunday}, {"segunda", DayOfWeek.Monday}, {"terca", DayOfWeek.Tuesday},
                {"quarta", DayOfWeek.Wednesday}, {"quinta", DayOfWeek.Thursday}, {"sexta", DayOfWeek.Friday},
                {"sabado", DayOfWeek.Saturday}
            };

            foreach (var dia in diasDaSemana)
            {
                if (dataTexto.Contains(dia.Key))
                {
                    var diaAlvo = dia.Value;
                    var dataResultado = hoje;
                    // Avança dia a dia até encontrar a próxima ocorrência do dia da semana alvo
                    while (dataResultado.DayOfWeek != diaAlvo)
                    {
                        dataResultado = dataResultado.AddDays(1);
                    }

                    // Se o dia encontrado for hoje, e o usuário não disse "hoje", pula para a próxima semana.
                    // Se o usuário disse "que vem", também pula para a próxima semana.
                    if (dataResultado == hoje || dataTexto.Contains("que vem") || dataTexto.Contains("proxima"))
                    {
                        dataResultado = dataResultado.AddDays(7);
                    }
                    return dataResultado;
                }
            }

            if (DateTime.TryParseExact(dataTexto, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dataEspecifica))
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
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) =================

