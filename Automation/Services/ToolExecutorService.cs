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
        public string FiltroData { get; set; }
        public string NovaData { get; set; }
        public string NovoHorario { get; set; }
        public int? NovaQtdPessoas { get; set; }
        public bool? EhMudancaRelativa { get; set; }
    }

    public class ListarServicosArgs
    {
        public Guid IdConversa { get; set; }
        public string? Filtro { get; set; }
    }

    public class ConsultarServicoArgs
    {
        public Guid IdConversa { get; set; }
        public string NomeServico { get; set; } = string.Empty;
    }

    public class RegistrarInteresseServicoArgs
    {
        public Guid IdConversa { get; set; }
        public string NomeCliente { get; set; } = string.Empty;
        public string NomeServico { get; set; } = string.Empty;
        public string? Veiculo { get; set; }
        public string? Observacoes { get; set; }
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
        private readonly CentralRoutingService _centralRouting;
        private readonly ServicoCatalogProvider _catalogProvider;
        private readonly IServicoAtendimentoRepository _servicoAtendimentoRepository;
        private readonly IEstabelecimentoRepository _estabelecimentoRepository;

        public ToolExecutorService(
            ILogger<ToolExecutorService> logger,
            IConversationRepository conversationRepository,
            HandoverService handoverService,
            IReservaRepository reservaRepository,
            ReservaValidator reservaValidator,
            IClienteRepository clienteRepository,
            CentralRoutingService centralRouting,
            ServicoCatalogProvider catalogProvider,
            IServicoAtendimentoRepository servicoAtendimentoRepository,
            IEstabelecimentoRepository estabelecimentoRepository)
        {
            _logger = logger;
            _conversationRepository = conversationRepository;
            _handoverService = handoverService;
            _reservaRepository = reservaRepository;
            _reservaValidator = reservaValidator;
            _clienteRepository = clienteRepository;
            _centralRouting = centralRouting;
            _catalogProvider = catalogProvider;
            _servicoAtendimentoRepository = servicoAtendimentoRepository;
            _estabelecimentoRepository = estabelecimentoRepository;
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

        public async Task<object[]> GetDeclaredToolsAsync(Guid idConversa)
        {
            var scope = await _centralRouting.ResolveEffectiveScopeAsync(idConversa);
            if (scope == null || scope.IdEstabelecimento == Guid.Empty)
                return GetDeclaredTools(idConversa);

            var modulos = await _estabelecimentoRepository.ObterModulosAtivosAsync(scope.IdEstabelecimento);
            var temServicos = modulos.Any(m => string.Equals(m, "Servicos", StringComparison.OrdinalIgnoreCase));
            var temReserva = modulos.Any(m => string.Equals(m, "Reserva", StringComparison.OrdinalIgnoreCase));

            var tools = new List<object>();

            if (temServicos)
                tools.AddRange(BuildServicosTools(idConversa.ToString()));

            if (temReserva)
                tools.AddRange(GetDeclaredTools(idConversa));

            // escalar_para_humano sempre presente
            if (!tools.Any())
                tools.AddRange(GetDeclaredTools(idConversa));

            return tools.ToArray();
        }

        private static object[] BuildServicosTools(string idConversaString) => new object[]
        {
            new {
                type = "function",
                name = "listar_servicos",
                description = @"Lista os serviços que o estabelecimento realiza.

QUANDO USAR:
- Cliente perguntar quais serviços são feitos
- Cliente perguntar o que o estabelecimento oferece
- Cliente quiser ver o catálogo completo

Parâmetro filtro é opcional — use quando cliente mencionou um tipo (ex: 'suspensão', 'elétrico').",
                parameters = new {
                    type = "object",
                    properties = new {
                        idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                        filtro = new { type = "string", description = "Palavra-chave opcional para filtrar (ex: 'suspensão', 'freio')" }
                    },
                    required = new[] { "idConversa" }
                }
            },
            new {
                type = "function",
                name = "consultar_servico",
                description = @"Retorna detalhes de um serviço específico: preço, tempo, variação por veículo, marcas de peça disponíveis.

QUANDO USAR:
- Cliente perguntar preço de um serviço específico
- Cliente perguntar tempo de execução
- Cliente perguntar sobre marcas de peça
- Cliente quiser saber mais sobre um serviço pelo nome",
                parameters = new {
                    type = "object",
                    properties = new {
                        idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                        nomeServico = new { type = "string", description = "Nome do serviço como o cliente mencionou" }
                    },
                    required = new[] { "idConversa", "nomeServico" }
                }
            },
            new {
                type = "function",
                name = "registrar_interesse_servico",
                description = @"Registra o interesse do cliente em um serviço e aciona a equipe.

QUANDO USAR:
- Cliente quiser agendar um serviço
- Cliente quiser orçamento formal
- Cliente confirmar interesse explícito

SEMPRE colete antes: nome do cliente, nome do serviço, veículo (se o serviço variar por veículo).",
                parameters = new {
                    type = "object",
                    properties = new {
                        idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                        nomeCliente = new { type = "string", description = "Nome completo do cliente" },
                        nomeServico = new { type = "string", description = "Nome do serviço de interesse" },
                        veiculo = new { type = "string", description = "Marca e modelo do veículo (se o serviço variar por veículo)" },
                        observacoes = new { type = "string", description = "Outras informações relevantes do cliente" }
                    },
                    required = new[] { "idConversa", "nomeCliente", "nomeServico" }
                }
            },
            new {
                type = "function",
                name = "escalar_para_humano",
                description = "Transfere a conversa para um atendente humano. Só executar após confirmação EXPLÍCITA do cliente.",
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

        private string BuildJsonReply(string reply, bool? reservaConfirmada = null)
        {
            if (reservaConfirmada.HasValue)
            {
                var objComConfirmacao = new { acao = "responder", reply, reserva_confirmada = reservaConfirmada.Value };
                return JsonSerializer.Serialize(objComConfirmacao, JsonOptions);
            }

            var obj = new { acao = "responder", reply };
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

                    case "listar_servicos":
                        var listarSvArgs = JsonSerializer.Deserialize<ListarServicosArgs>(argsJson, JsonOptions);
                        if (listarSvArgs == null)
                            return BuildJsonReply("Argumentos inválidos.");
                        return await HandleListarServicos(listarSvArgs);

                    case "consultar_servico":
                        var consultarSvArgs = JsonSerializer.Deserialize<ConsultarServicoArgs>(argsJson, JsonOptions);
                        if (consultarSvArgs == null)
                            return BuildJsonReply("Argumentos inválidos.");
                        return await HandleConsultarServico(consultarSvArgs);

                    case "registrar_interesse_servico":
                        var registrarSvArgs = JsonSerializer.Deserialize<RegistrarInteresseServicoArgs>(argsJson, JsonOptions);
                        if (registrarSvArgs == null)
                            return BuildJsonReply("Argumentos inválidos.");
                        return await HandleRegistrarInteresseServico(registrarSvArgs);

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
                    return BuildJsonReply(validationResult.MensagemErro, reservaConfirmada: false);
                }

                return BuildJsonReply(validationResult.MensagemErro);
            }

            var dataReserva = validationResult.DataCalculada.Value;
            var horaConvertida = validationResult.HoraCalculada.Value;

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

            var escopo = await _centralRouting.ResolveEffectiveScopeAsync(args.IdConversa, conversa);
            if (escopo == null || escopo.IdCliente == Guid.Empty || escopo.IdEstabelecimento == Guid.Empty)
            {
                return BuildJsonReply("Não consegui identificar seus dados.\n\nPode tentar novamente? 😊");
            }

            var telefone = escopo.TelefoneCliente ?? conversa.TelefoneCliente;
            var idCliente = escopo.IdCliente;
            var idEstabelecimento = escopo.IdEstabelecimento;

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
                if (!reservaMesmoDia.Id.HasValue)
                {
                    throw new InvalidOperationException("Reserva existente nao possui identificador.");
                }

                idReserva = reservaMesmoDia.Id.Value;

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
                    NomeCliente = args.NomeCompleto,
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

            var escopo = await _centralRouting.ResolveEffectiveScopeAsync(args.IdConversa, conversa);
            if (escopo == null || escopo.IdCliente == Guid.Empty || escopo.IdEstabelecimento == Guid.Empty)
            {
                return BuildJsonReply("Não consegui identificar seus dados.\n\nPode tentar novamente? 😊");
            }

            var idCliente = escopo.IdCliente;
            var idEstabelecimento = escopo.IdEstabelecimento;

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

            if (args.CodigoReserva.HasValue)
            {
                var reservaPorCodigo = reservasAtivas.FirstOrDefault(r => r.Codigo == args.CodigoReserva.Value.ToString());

                if (reservaPorCodigo == null)
                {
                    _logger.LogWarning(
                        "[Conversa={Conversa}] Código #{Codigo} não encontrado nas reservas ativas do cliente",
                        args.IdConversa,
                        args.CodigoReserva.Value);

                    return BuildJsonReply($"Não encontrei a reserva #{args.CodigoReserva.Value} no seu nome. 😕\n\nQuer que eu liste suas reservas ativas? 😊");
                }

                var reservaId = reservaPorCodigo.Id ?? throw new InvalidOperationException("Reserva encontrada sem identificador.");

                await _reservaRepository.CancelarReservaAsync(reservaId);
                await _conversationRepository.LimparContextoAsync(args.IdConversa);

                var dataFormatada = reservaPorCodigo.DataReserva.ToString("dd/MM/yyyy");
                var horaFormatada = reservaPorCodigo.HoraInicio.ToString(@"hh\:mm");

                _logger.LogInformation(
                    "[Conversa={Conversa}] Reserva #{IdReserva} cancelada via código. Contexto limpo. Motivo: {Motivo}",
                    args.IdConversa,
                    reservaId,
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
                    args.IdConversa);

                await _conversationRepository.SalvarContextoAsync(args.IdConversa, new ConversationContext
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
                msg.AppendLine("D) 💬 Perguntar sobre cardápio, endereço, horários...");
                msg.AppendLine();
                msg.Append("Responda com a letra (A, B, C ou D) 📝");

                return BuildJsonReply(msg.ToString());
            }
            else
            {
                await _conversationRepository.SalvarContextoAsync(args.IdConversa, new ConversationContext
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
                msgLista.AppendLine("D) 💬 Perguntar sobre cardápio, endereço, horários...");
                msgLista.AppendLine();
                msgLista.Append("Responda com a letra (A, B, C ou D) 📝");

                return BuildJsonReply(msgLista.ToString());
            }
        }

        private async Task<string> HandleAtualizarReserva(AtualizarReservaArgs args)
        {
            _logger.LogInformation(
                "[Conversa={Conversa}] HandleAtualizarReserva chamado - Código={Codigo}, Filtro={Filtro}",
                args.IdConversa,
                args.CodigoReserva,
                args.FiltroData);

            var conversa = await _conversationRepository.ObterPorIdAsync(args.IdConversa);
            if (conversa == null)
            {
                return BuildJsonReply("Não consegui localizar nossa conversa.\n\nPode tentar novamente? 😊");
            }

            var escopo = await _centralRouting.ResolveEffectiveScopeAsync(args.IdConversa, conversa);
            if (escopo == null || escopo.IdCliente == Guid.Empty || escopo.IdEstabelecimento == Guid.Empty)
            {
                return BuildJsonReply("Não consegui identificar seus dados.\n\nPode tentar novamente? 😊");
            }

            var idCliente = escopo.IdCliente;
            var idEstabelecimento = escopo.IdEstabelecimento;

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
                _logger.LogInformation("[Conversa={Conversa}] Cliente não possui reservas ativas para atualizar", args.IdConversa);
                return BuildJsonReply("Não encontrei nenhuma reserva ativa no seu nome 🤔\n\nQuer fazer uma nova reserva? 😊");
            }

            // 🔍 BUSCAR RESERVA POR CÓDIGO (se fornecido)
            Reserva reservaParaAtualizar = null;

            if (args.CodigoReserva.HasValue)
            {
                reservaParaAtualizar = reservasAtivas.FirstOrDefault(r => r.Codigo == args.CodigoReserva.Value.ToString());

                if (reservaParaAtualizar == null)
                {
                    _logger.LogWarning(
                        "[Conversa={Conversa}] Código #{Codigo} não encontrado nas reservas ativas",
                        args.IdConversa,
                        args.CodigoReserva.Value);

                    return BuildJsonReply($"Não encontrei a reserva #{args.CodigoReserva.Value} no seu nome. 😕\n\nQuer que eu liste suas reservas ativas? 😊");
                }
            }
            else if (reservasAtivas.Count == 1)
            {
                // Cliente tem apenas 1 reserva - usar ela
                reservaParaAtualizar = reservasAtivas.First();
            }
            else
            {
                // Múltiplas reservas sem código - pedir para especificar
                _logger.LogInformation(
                    "[Conversa={Conversa}] Cliente tem {Count} reservas mas não especificou qual - pedindo código",
                    args.IdConversa,
                    reservasAtivas.Count);

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

                msgLista.Append("Qual reserva você quer alterar? Me informe o código 😊");

                return BuildJsonReply(msgLista.ToString());
            }

            // ✅ APLICAR ALTERAÇÕES
            bool houveAlteracao = false;
            var alteracoes = new StringBuilder();
            alteracoes.AppendLine($"✅ Reserva #{reservaParaAtualizar.Codigo} atualizada com sucesso!");
            alteracoes.AppendLine();

            // Atualizar HORÁRIO
            if (!string.IsNullOrWhiteSpace(args.NovoHorario))
            {
                if (TimeSpan.TryParseExact(args.NovoHorario, @"HH\:mm", CultureInfo.InvariantCulture, out var novoHorario))
                {
                    var horarioAntigo = reservaParaAtualizar.HoraInicio;
                    reservaParaAtualizar.HoraInicio = novoHorario;
                    alteracoes.AppendLine($"⏰ Horário: {horarioAntigo:hh\\:mm} → {novoHorario:hh\\:mm}");
                    houveAlteracao = true;
                }
            }

            // Atualizar QUANTIDADE DE PESSOAS
            if (args.NovaQtdPessoas.HasValue)
            {
                var qtdAtual = reservaParaAtualizar.QtdPessoas ?? 0;
                int qtdFinal;

                if (args.EhMudancaRelativa == true)
                {
                    // Mudança relativa: adicionar/tirar
                    qtdFinal = qtdAtual + args.NovaQtdPessoas.Value;
                }
                else
                {
                    // Mudança absoluta: valor final
                    qtdFinal = args.NovaQtdPessoas.Value;
                }

                if (qtdFinal < 1)
                {
                    return BuildJsonReply("Não é possível ter menos de 1 pessoa na reserva 😅\n\nQual seria a quantidade correta? 😊");
                }

                if (qtdFinal > 100)
                {
                    return BuildJsonReply("Para grupos grandes (mais de 100 pessoas), por favor entre em contato diretamente conosco! 😊");
                }

                if (qtdAtual != qtdFinal)
                {
                    reservaParaAtualizar.QtdPessoas = qtdFinal;
                    alteracoes.AppendLine($"👥 Pessoas: {qtdAtual} → {qtdFinal}");
                    houveAlteracao = true;
                }
            }

            // Atualizar DATA (se fornecida)
            if (!string.IsNullOrWhiteSpace(args.NovaData))
            {
                // TODO: Implementar parse de data usando ParseDataRelativa
                // Por enquanto, informar que precisa de implementação
                return BuildJsonReply("Alteração de data será implementada em breve. Por enquanto, você pode alterar horário e quantidade de pessoas. 😊");
            }

            if (!houveAlteracao)
            {
                return BuildJsonReply("Nenhuma alteração foi especificada. O que você gostaria de mudar? 😊");
            }

            // 💾 SALVAR NO BANCO
            reservaParaAtualizar.DataAtualizacao = DateTime.UtcNow;
            await _reservaRepository.AtualizarAsync(reservaParaAtualizar);

            _logger.LogInformation(
                "[Conversa={Conversa}] Reserva #{Codigo} atualizada com sucesso",
                args.IdConversa,
                reservaParaAtualizar.Codigo);

            // Limpar contexto após alteração bem-sucedida
            await _conversationRepository.LimparContextoAsync(args.IdConversa);

            alteracoes.AppendLine();
            alteracoes.AppendLine("📋 Dados atualizados:");
            alteracoes.AppendLine($"📅 Data: {reservaParaAtualizar.DataReserva:dd/MM/yyyy}");
            alteracoes.AppendLine($"⏰ Horário: {reservaParaAtualizar.HoraInicio:hh\\:mm}");
            alteracoes.AppendLine($"👥 Pessoas: {reservaParaAtualizar.QtdPessoas}");
            alteracoes.AppendLine();
            alteracoes.Append("Nos vemos lá! 😊✨");

            return BuildJsonReply(alteracoes.ToString());
        }

        private async Task<string> HandleListarReservas(Guid idConversa)
        {
            var conversa = await _conversationRepository.ObterPorIdAsync(idConversa);
            if (conversa == null)
            {
                return BuildJsonReply("Não consegui localizar nossa conversa.\n\nPode tentar novamente? 😊");
            }

            var escopo = await _centralRouting.ResolveEffectiveScopeAsync(idConversa, conversa);
            if (escopo == null || escopo.IdCliente == Guid.Empty || escopo.IdEstabelecimento == Guid.Empty)
            {
                return BuildJsonReply("Não consegui identificar seus dados.\n\nPode tentar novamente? 😊");
            }

            var idCliente = escopo.IdCliente;
            var idEstabelecimento = escopo.IdEstabelecimento;

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
                _logger.LogInformation("[Conversa={Conversa}] Cliente não possui reservas ativas", idConversa);
                return BuildJsonReply("Você não tem nenhuma reserva ativa no momento 🤔\n\nQuer fazer uma nova reserva? 😊");
            }

            if (reservasAtivas.Count == 1)
            {
                var reserva = reservasAtivas.First();
                var nomeReserva = reserva.NomeCliente ?? "Cliente";

                _logger.LogInformation(
                    "[Conversa={Conversa}] Cliente tem 1 reserva - mostrando com menu A/B/C",
                    idConversa);

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
                msg.AppendLine("D) 💬 Perguntar sobre cardápio, endereço, horários...");
                msg.AppendLine();
                msg.Append("Responda com a letra (A, B, C ou D) 📝");

                return BuildJsonReply(msg.ToString());
            }
            else
            {
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
                msgLista.AppendLine("D) 💬 Perguntar sobre cardápio, endereço, horários...");
                msgLista.AppendLine();
                msgLista.Append("Responda com a letra (A, B, C ou D) 📝");

                return BuildJsonReply(msgLista.ToString());
            }
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

            _logger.LogInformation(
                "[Conversa={Conversa}] Escalonamento para humano solicitado. Motivo: {Motivo}",
                args.IdConversa,
                args.Motivo);

            var msg = new StringBuilder();
            msg.AppendLine("Transferindo você para um atendente humano 👤");
            msg.AppendLine();
            msg.Append("Em instantes alguém irá atendê-lo! 😊");

            return BuildJsonReply(msg.ToString());
        }

        private async Task<string> HandleListarServicos(ListarServicosArgs args)
        {
            var scope = await _centralRouting.ResolveEffectiveScopeAsync(args.IdConversa);
            if (scope == null)
                return BuildJsonReply("Não consegui identificar o estabelecimento.");

            var catalogo = await _catalogProvider.ObterCatalogoVisivelAsync(scope.IdEstabelecimento);
            if (catalogo.Count == 0)
                return BuildJsonReply("Não encontrei serviços cadastrados no momento.");

            IEnumerable<ServicoCatalogItem> lista = catalogo;
            if (!string.IsNullOrWhiteSpace(args.Filtro))
            {
                lista = catalogo.Where(s =>
                    s.Nome.Contains(args.Filtro!, StringComparison.OrdinalIgnoreCase) ||
                    s.PalavrasChave.Any(k => k.Contains(args.Filtro!, StringComparison.OrdinalIgnoreCase)) ||
                    (s.Tipo ?? string.Empty).Contains(args.Filtro!, StringComparison.OrdinalIgnoreCase));
            }

            var itens = lista.ToList();
            if (itens.Count == 0)
                return BuildJsonReply($"Não encontrei serviços relacionados a \"{args.Filtro}\".");

            var sb = new StringBuilder();
            sb.AppendLine(string.IsNullOrWhiteSpace(args.Filtro)
                ? "📋 Serviços disponíveis:\n"
                : $"📋 Serviços disponíveis ({args.Filtro}):\n");

            foreach (var s in itens)
            {
                sb.Append($"• {s.Nome}");
                if (s.ValorCentavos.HasValue)
                    sb.Append($" — R$ {s.ValorCentavos.Value / 100m:N2}");
                else if (s.ValorMinimoCentavos.HasValue && s.ValorMaximoCentavos.HasValue)
                    sb.Append($" — R$ {s.ValorMinimoCentavos.Value / 100m:N2} a R$ {s.ValorMaximoCentavos.Value / 100m:N2}");
                if (s.DiferePorVeiculo)
                    sb.Append(" *(varia por veículo)*");
                sb.AppendLine();
            }

            sb.AppendLine("\nQuer saber mais sobre algum desses serviços?");
            return BuildJsonReply(sb.ToString());
        }

        private async Task<string> HandleConsultarServico(ConsultarServicoArgs args)
        {
            var scope = await _centralRouting.ResolveEffectiveScopeAsync(args.IdConversa);
            if (scope == null)
                return BuildJsonReply("Não consegui identificar o estabelecimento.");

            var catalogo = await _catalogProvider.ObterCatalogoVisivelAsync(scope.IdEstabelecimento);
            var nomeNorm = args.NomeServico.Trim().ToLowerInvariant();

            var servico = catalogo.FirstOrDefault(s =>
                s.Nome.ToLowerInvariant().Contains(nomeNorm) ||
                s.PalavrasChave.Any(k =>
                    k.ToLowerInvariant().Contains(nomeNorm) ||
                    nomeNorm.Contains(k.ToLowerInvariant())));

            if (servico == null)
                return BuildJsonReply($"Não encontrei o serviço \"{args.NomeServico}\". Quer ver a lista completa?");

            var sb = new StringBuilder();
            sb.AppendLine($"🔧 *{servico.Nome}*");

            if (!string.IsNullOrWhiteSpace(servico.Descricao))
                sb.AppendLine(servico.Descricao);

            if (servico.DiferePorVeiculo && servico.Veiculos.Count > 0)
            {
                sb.AppendLine("\nValores por veículo:");
                foreach (var v in servico.Veiculos.Where(v => v.Compativel))
                {
                    sb.Append($"  • {v.NomeExibicao}");
                    if (v.ValorCentavos.HasValue)
                        sb.Append($": R$ {v.ValorCentavos.Value / 100m:N2}");
                    else if (v.ValorMinimoCentavos.HasValue)
                        sb.Append($": a partir de R$ {v.ValorMinimoCentavos.Value / 100m:N2}");
                    sb.AppendLine();
                }
            }
            else if (servico.ValorCentavos.HasValue)
            {
                sb.AppendLine($"\n💰 Valor: R$ {servico.ValorCentavos.Value / 100m:N2}");
            }
            else if (servico.ValorMinimoCentavos.HasValue)
            {
                sb.AppendLine($"\n💰 Valor: a partir de R$ {servico.ValorMinimoCentavos.Value / 100m:N2}");
            }

            if (servico.DuracaoMinutos > 0)
                sb.AppendLine($"⏱ Tempo médio: {servico.DuracaoMinutos} min");

            if (servico.PermiteAgendamento)
                sb.AppendLine("\nPosso registrar seu interesse para que a equipe entre em contato. Quer agendar?");

            return BuildJsonReply(sb.ToString());
        }

        private async Task<string> HandleRegistrarInteresseServico(RegistrarInteresseServicoArgs args)
        {
            var scope = await _centralRouting.ResolveEffectiveScopeAsync(args.IdConversa);
            if (scope == null)
                return BuildJsonReply("Não consegui identificar o estabelecimento.");

            var atendimento = new ServicoAtendimento
            {
                Id = Guid.NewGuid(),
                IdEstabelecimento = scope.IdEstabelecimento,
                IdConversa = args.IdConversa,
                IdCliente = scope.IdCliente,
                TelefoneE164 = scope.TelefoneCliente ?? string.Empty,
                NomeCliente = args.NomeCliente.Trim(),
                NomeServico = args.NomeServico.Trim(),
                IntencaoPrincipal = "agendamento",
                Status = "aguardando_interno",
                DadosExtras = new Dictionary<string, object?>
                {
                    ["veiculo"] = args.Veiculo,
                    ["observacoes"] = args.Observacoes
                },
                DataCriacao = DateTime.UtcNow,
                DataAtualizacao = DateTime.UtcNow,
                DataHandover = DateTime.UtcNow
            };

            await _servicoAtendimentoRepository.CriarAsync(atendimento);

            var contextoResumo = $"Interesse em: {args.NomeServico}" +
                                  (string.IsNullOrWhiteSpace(args.Veiculo) ? string.Empty : $" | Veículo: {args.Veiculo}") +
                                  (string.IsNullOrWhiteSpace(args.Observacoes) ? string.Empty : $" | Obs: {args.Observacoes}");

            var detalhes = new HandoverContextDto
            {
                ClienteNome = args.NomeCliente.Trim(),
                Motivo = "Interesse em serviço",
                Contexto = contextoResumo
            };

            await _handoverService.ProcessarMensagensTelegramAsync(args.IdConversa, null, false, detalhes);

            var primeiroNome = args.NomeCliente.Trim().Split(' ')[0];
            return BuildJsonReply(
                $"✅ Perfeito, {primeiroNome}! Registrei seu interesse em *{args.NomeServico}*.\n\n" +
                "Nossa equipe vai entrar em contato em breve para confirmar os detalhes. 😊");
        }
    }
}
