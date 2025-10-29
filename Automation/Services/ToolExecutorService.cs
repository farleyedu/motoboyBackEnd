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

                // ✅ CORREÇÃO: Salvar nome do cliente informado na reserva
                reservaMesmoDia.NomeCliente = args.NomeCompleto;
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
                    NomeCliente = args.NomeCompleto,  // ✅ CORREÇÃO: Salvar nome informado
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
            // Buscar a reserva recém-criada para pegar o Codigo
            var reservaCriada = await _reservaRepository.BuscarPorIdAsync(idReserva);
            var codigoExibir = reservaCriada?.Codigo ?? idReserva.ToString();
            builder.AppendLine($"🎫 Seu código de reserva é o #{codigoExibir}.");
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
                msg.AppendLine($"🎫 Código: #{reservaPorCodigo.Codigo}");
                msg.AppendLine($"📅 Data: {dataFormatada}");
                msg.AppendLine($"⏰ Horário: {horaFormatada}");
                msg.AppendLine();
                msg.Append("Se mudar de ideia, estamos aqui! 😊");

                return BuildJsonReply(msg.ToString());
            }

            if (reservasAtivas.Count == 1)
        {
            var reserva = reservasAtivas.First();
            var nomeReserva = reserva.NomeCliente ?? "Cliente";

            _logger.LogInformation(
                "[Conversa={Conversa}] Cliente tem 1 reserva - mostrando com menu A/B/C",
                idConversa);

            // Salvar contexto com informações da reserva para facilitar escolha
            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = "aguardando_escolha_acao",
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
            msg.AppendLine("📋 Sua reserva ativa:");
            msg.AppendLine();
            msg.AppendLine($"🎫 Código: #{reserva.Codigo}");
            msg.AppendLine($"👤 Nome: {nomeReserva}");
            msg.AppendLine($"📅 Data: {reserva.DataReserva:dd/MM/yyyy} ({reserva.DataReserva:dddd})");
            msg.AppendLine($"⏰ Horário: {reserva.HoraInicio:hh\\:mm}");
            msg.AppendLine($"👥 Pessoas: {reserva.QtdPessoas}");
            msg.AppendLine();
            msg.AppendLine("━━━━━━━━━━━━━━━━━━━━━━");
            msg.AppendLine("O que você quer fazer? 😊");
            msg.AppendLine();
            msg.AppendLine("A) 🆕 Criar nova reserva");
            msg.AppendLine("B) ❌ Cancelar esta reserva");
            msg.AppendLine("C) ✏️ Alterar esta reserva");
            msg.AppendLine();
            msg.Append("Responda com a letra (A, B ou C) 📝");

            return BuildJsonReply(msg.ToString());
        }
        else
        {
            // Salvar contexto sem amarrar reserva específica ainda
            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = "aguardando_escolha_acao",
                DadosColetados = new Dictionary<string, object>
                {
                    { "tem_multiplas_reservas", true },
                    { "total_reservas", reservasAtivas.Count }
                },
                ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
            });

            var msgLista = new StringBuilder();
            msgLista.AppendLine($"📋 Você tem {reservasAtivas.Count} reservas ativas:");
            msgLista.AppendLine();

            foreach (var r in reservasAtivas)
            {
                msgLista.AppendLine($"🎫 Reserva #{r.Codigo}");
                msgLista.AppendLine($"📅 {r.DataReserva:dd/MM/yyyy} ({r.DataReserva:dddd})");
                msgLista.AppendLine($"⏰ {r.HoraInicio:hh\\:mm}");
                msgLista.AppendLine($"👥 {r.QtdPessoas} pessoas");
                msgLista.AppendLine();
            }

            msgLista.AppendLine("━━━━━━━━━━━━━━━━━━━━━━");
            msgLista.AppendLine("O que você quer fazer? 😊");
            msgLista.AppendLine();
            msgLista.AppendLine("A) 🆕 Criar nova reserva");
            msgLista.AppendLine("B) ❌ Cancelar uma reserva");
            msgLista.AppendLine("C) ✏️ Alterar uma reserva");
            msgLista.AppendLine();
            msgLista.Append("Responda com a letra (A, B ou C) 📝");

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
