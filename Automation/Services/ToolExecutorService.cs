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
using APIBack.DTOs.Agendamentos;
using APIBack.Model;
using APIBack.Repository.Interface;
using APIBack.Service;
using APIBack.Service.Interface;
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
        public string? Veiculo { get; set; }
        public string? VeiculoMarca { get; set; }
        public string? VeiculoModelo { get; set; }
        public string? MarcaPeca { get; set; }
    }

    public class ConsultarFaqArgs
    {
        public Guid IdConversa { get; set; }
        public string Pergunta { get; set; } = string.Empty;
    }

    public class RegistrarInteresseServicoArgs
    {
        public Guid IdConversa { get; set; }
        public string NomeCliente { get; set; } = string.Empty;
        public string NomeServico { get; set; } = string.Empty;
        public string? Veiculo { get; set; }
        public string? VeiculoMarca { get; set; }
        public string? VeiculoModelo { get; set; }
        public string? MarcaPeca { get; set; }
        public string? Observacoes { get; set; }
    }

    public class ConsultarDisponibilidadeOficinaArgs
    {
        public Guid IdConversa { get; set; }
        public string NomeServico { get; set; } = string.Empty;
        public string Data { get; set; } = string.Empty;
        public int? DuracaoMinutos { get; set; }
        public long? ProfissionalId { get; set; }
    }

    public class CriarAgendamentoOficinaArgs
    {
        public Guid IdConversa { get; set; }
        public string NomeCliente { get; set; } = string.Empty;
        public string NomeServico { get; set; } = string.Empty;
        public string Data { get; set; } = string.Empty;
        public string HoraInicio { get; set; } = string.Empty;
        public int? DuracaoMinutos { get; set; }
        public long? ProfissionalId { get; set; }
        public string? Veiculo { get; set; }
        public string? VeiculoMarca { get; set; }
        public string? VeiculoModelo { get; set; }
        public string? MarcaPeca { get; set; }
        public string? Observacoes { get; set; }
    }

    public class CodigoAgendamentoOficinaArgs
    {
        public Guid IdConversa { get; set; }
        public string? CodigoAgendamento { get; set; }
        public string? MotivoCliente { get; set; }
    }

    public class RemarcarAgendamentoOficinaArgs
    {
        public Guid IdConversa { get; set; }
        public string? CodigoAgendamento { get; set; }
        public string NovaData { get; set; } = string.Empty;
        public string NovoHorario { get; set; } = string.Empty;
        public int? DuracaoMinutos { get; set; }
        public long? ProfissionalId { get; set; }
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
        private readonly FaqCatalogProvider _faqCatalogProvider;
        private readonly IServicoAtendimentoRepository _servicoAtendimentoRepository;
        private readonly IEstabelecimentoRepository _estabelecimentoRepository;
        private readonly IOficinaAgendamentoService _oficinaAgendamentoService;

        public ToolExecutorService(
            ILogger<ToolExecutorService> logger,
            IConversationRepository conversationRepository,
            HandoverService handoverService,
            IReservaRepository reservaRepository,
            ReservaValidator reservaValidator,
            IClienteRepository clienteRepository,
            CentralRoutingService centralRouting,
            ServicoCatalogProvider catalogProvider,
            FaqCatalogProvider faqCatalogProvider,
            IServicoAtendimentoRepository servicoAtendimentoRepository,
            IEstabelecimentoRepository estabelecimentoRepository,
            IOficinaAgendamentoService oficinaAgendamentoService)
        {
            _logger = logger;
            _conversationRepository = conversationRepository;
            _handoverService = handoverService;
            _reservaRepository = reservaRepository;
            _reservaValidator = reservaValidator;
            _clienteRepository = clienteRepository;
            _centralRouting = centralRouting;
            _catalogProvider = catalogProvider;
            _faqCatalogProvider = faqCatalogProvider;
            _servicoAtendimentoRepository = servicoAtendimentoRepository;
            _estabelecimentoRepository = estabelecimentoRepository;
            _oficinaAgendamentoService = oficinaAgendamentoService;
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
            var temFaq = modulos.Any(m => string.Equals(m, "FAQ", StringComparison.OrdinalIgnoreCase));
            var temReserva = modulos.Any(m =>
                string.Equals(m, "Reserva", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m, "Agendamentos", StringComparison.OrdinalIgnoreCase));
            var tipoEstabelecimento = await _estabelecimentoRepository.ObterTipoEstabelecimentoAsync(scope.IdEstabelecimento);
            var ehOficina = string.Equals(tipoEstabelecimento, "oficina", StringComparison.OrdinalIgnoreCase);

            var tools = new List<object>();

            if (temServicos)
                tools.AddRange(BuildServicosTools(idConversa.ToString()));

            if (temFaq)
                tools.AddRange(BuildFaqTools(idConversa.ToString()));

            if (temReserva && !ehOficina)
                tools.AddRange(GetDeclaredTools(idConversa));

            if (ehOficina && temServicos && temReserva)
                tools.AddRange(BuildOficinaAgendamentoTools(idConversa.ToString()));

            // escalar_para_humano sempre presente
            if (!tools.Any())
                tools.AddRange(GetDeclaredTools(idConversa));

            return tools.ToArray();
        }

        private static object[] BuildOficinaAgendamentoTools(string idConversaString) => new object[]
        {
            new {
                type = "function",
                name = "consultar_disponibilidade_oficina",
                description = @"Consulta horários disponíveis para agendar serviço de oficina.

Use quando o cliente pedir horário, quiser agendar ou perguntar disponibilidade. Antes, identifique o serviço e a data desejada.",
                parameters = new {
                    type = "object",
                    properties = new {
                        idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                        nomeServico = new { type = "string", description = "Nome do serviço desejado" },
                        data = new { type = "string", description = "Data em yyyy-MM-dd" },
                        duracaoMinutos = new { type = "integer", description = "Duração do serviço em minutos, se conhecida" },
                        profissionalId = new { type = "integer", description = "ID do profissional se o cliente escolheu um" }
                    },
                    required = new[] { "idConversa", "nomeServico", "data" }
                }
            },
            new {
                type = "function",
                name = "criar_agendamento_oficina",
                description = @"Cria um agendamento real de oficina após o cliente escolher explicitamente um horário disponível.",
                parameters = new {
                    type = "object",
                    properties = new {
                        idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                        nomeCliente = new { type = "string", description = "Nome do cliente" },
                        nomeServico = new { type = "string", description = "Nome do serviço" },
                        data = new { type = "string", description = "Data em yyyy-MM-dd" },
                        horaInicio = new { type = "string", description = "Hora escolhida em HH:mm" },
                        duracaoMinutos = new { type = "integer", description = "Duração do serviço em minutos, se conhecida" },
                        profissionalId = new { type = "integer", description = "ID do profissional se aplicável" },
                        veiculo = new { type = "string", description = "Texto completo do veículo" },
                        veiculoMarca = new { type = "string", description = "Marca do veículo" },
                        veiculoModelo = new { type = "string", description = "Modelo do veículo" },
                        marcaPeca = new { type = "string", description = "Marca da peça escolhida" },
                        observacoes = new { type = "string", description = "Observações do cliente" }
                    },
                    required = new[] { "idConversa", "nomeCliente", "nomeServico", "data", "horaInicio" }
                }
            },
            new {
                type = "function",
                name = "listar_agendamentos_oficina",
                description = "Lista agendamentos ativos de oficina do cliente atual.",
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
                name = "cancelar_agendamento_oficina",
                description = "Cancela um agendamento de oficina após confirmação explícita do cliente.",
                parameters = new {
                    type = "object",
                    properties = new {
                        idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                        codigoAgendamento = new { type = "string", description = "Código do agendamento, se informado" },
                        motivoCliente = new { type = "string", description = "Motivo do cancelamento, se informado" }
                    },
                    required = new[] { "idConversa" }
                }
            },
            new {
                type = "function",
                name = "remarcar_agendamento_oficina",
                description = "Remarca um agendamento de oficina após o cliente escolher nova data e novo horário.",
                parameters = new {
                    type = "object",
                    properties = new {
                        idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                        codigoAgendamento = new { type = "string", description = "Código do agendamento, se informado" },
                        novaData = new { type = "string", description = "Nova data em yyyy-MM-dd" },
                        novoHorario = new { type = "string", description = "Novo horário em HH:mm" },
                        duracaoMinutos = new { type = "integer", description = "Duração em minutos, se conhecida" },
                        profissionalId = new { type = "integer", description = "ID do profissional se aplicável" }
                    },
                    required = new[] { "idConversa", "novaData", "novoHorario" }
                }
            }
        };

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
                        nomeServico = new { type = "string", description = "Nome do serviço como o cliente mencionou" },
                        veiculo = new { type = "string", description = "Texto completo do veículo, se o cliente já informou" },
                        veiculoMarca = new { type = "string", description = "Marca do veículo, se já conhecida" },
                        veiculoModelo = new { type = "string", description = "Modelo do veículo, se já conhecido" },
                        marcaPeca = new { type = "string", description = "Marca da peça escolhida ou mencionada pelo cliente" }
                    },
                    required = new[] { "idConversa", "nomeServico" }
                }
            },
            new {
                type = "function",
                name = "registrar_interesse_servico",
                description = @"Registra o interesse do cliente em um serviço e aciona a equipe.

QUANDO USAR:
- Cliente quiser orçamento formal
- Cliente confirmar interesse explícito
- Cliente quiser humano ou quando não for possível criar agendamento automático

IMPORTANTE PARA OFICINA:
- Se houver tools de oficina e o cliente quiser agendar, consulte disponibilidade e use criar_agendamento_oficina.
- Não use esta tool para agendar oficina quando existir horário disponível.

SEMPRE colete antes: nome do cliente, nome do serviço, veículo (se o serviço variar por veículo).",
                parameters = new {
                    type = "object",
                    properties = new {
                        idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                        nomeCliente = new { type = "string", description = "Nome completo do cliente" },
                        nomeServico = new { type = "string", description = "Nome do serviço de interesse" },
                        veiculo = new { type = "string", description = "Marca e modelo do veículo (se o serviço variar por veículo)" },
                        veiculoMarca = new { type = "string", description = "Marca do veículo quando coletada separadamente" },
                        veiculoModelo = new { type = "string", description = "Modelo do veículo quando coletado separadamente" },
                        marcaPeca = new { type = "string", description = "Marca da peça confirmada pelo cliente, quando aplicável" },
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

        private string BuildToolDataReply(
            string tool,
            string status,
            string reply,
            object? data = null,
            ConversationFichaAtual? fichaAtualSugerida = null,
            bool? reservaConfirmada = null)
        {
            var payload = new
            {
                acao = "responder",
                reply,
                reserva_confirmada = reservaConfirmada,
                tool,
                status,
                data,
                ficha_atual_sugerida = fichaAtualSugerida == null || !ConversationFichaAtualStore.HasMeaningfulData(fichaAtualSugerida)
                    ? null
                    : ConversationFichaAtualStore.Normalize(fichaAtualSugerida)
            };

            return JsonSerializer.Serialize(payload, JsonOptions);
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

                    case "consultar_faq":
                        var consultarFaqArgs = JsonSerializer.Deserialize<ConsultarFaqArgs>(argsJson, JsonOptions);
                        if (consultarFaqArgs == null)
                            return BuildJsonReply("Argumentos inválidos.");
                        return await HandleConsultarFaq(consultarFaqArgs);

                    case "registrar_interesse_servico":
                        var registrarSvArgs = JsonSerializer.Deserialize<RegistrarInteresseServicoArgs>(argsJson, JsonOptions);
                        if (registrarSvArgs == null)
                            return BuildJsonReply("Argumentos inválidos.");
                        return await HandleRegistrarInteresseServico(registrarSvArgs);

                    case "consultar_disponibilidade_oficina":
                        var disponibilidadeArgs = JsonSerializer.Deserialize<ConsultarDisponibilidadeOficinaArgs>(argsJson, JsonOptions);
                        if (disponibilidadeArgs == null)
                            return BuildJsonReply("Argumentos inválidos para consultar disponibilidade.");
                        return await HandleConsultarDisponibilidadeOficina(disponibilidadeArgs);

                    case "criar_agendamento_oficina":
                        var criarOficinaArgs = JsonSerializer.Deserialize<CriarAgendamentoOficinaArgs>(argsJson, JsonOptions);
                        if (criarOficinaArgs == null)
                            return BuildJsonReply("Argumentos inválidos para criar agendamento.");
                        return await HandleCriarAgendamentoOficina(criarOficinaArgs);

                    case "listar_agendamentos_oficina":
                        var listarOficinaArgs = JsonSerializer.Deserialize<Dictionary<string, Guid>>(argsJson, JsonOptions);
                        if (listarOficinaArgs == null || !listarOficinaArgs.TryGetValue("idConversa", out var idConvOficina))
                            return BuildJsonReply("Argumentos inválidos para listar agendamentos.");
                        return await HandleListarAgendamentosOficina(idConvOficina);

                    case "cancelar_agendamento_oficina":
                        var cancelarOficinaArgs = JsonSerializer.Deserialize<CodigoAgendamentoOficinaArgs>(argsJson, JsonOptions);
                        if (cancelarOficinaArgs == null)
                            return BuildJsonReply("Argumentos inválidos para cancelar agendamento.");
                        return await HandleCancelarAgendamentoOficina(cancelarOficinaArgs);

                    case "remarcar_agendamento_oficina":
                        var remarcarOficinaArgs = JsonSerializer.Deserialize<RemarcarAgendamentoOficinaArgs>(argsJson, JsonOptions);
                        if (remarcarOficinaArgs == null)
                            return BuildJsonReply("Argumentos inválidos para remarcar agendamento.");
                        return await HandleRemarcarAgendamentoOficina(remarcarOficinaArgs);

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

        public async Task<object[]> GetToolsForOpenAI(Guid idConversa)
        {
            var idConversaString = idConversa.ToString();
            var scope = await _centralRouting.ResolveEffectiveScopeAsync(idConversa);
            if (scope == null || scope.IdEstabelecimento == Guid.Empty)
            {
                return Array.Empty<object>();
            }

            var modulos = await _estabelecimentoRepository.ObterModulosAtivosAsync(scope.IdEstabelecimento);
            var temServicos = modulos.Any(m => string.Equals(m, "Servicos", StringComparison.OrdinalIgnoreCase));
            var temFaq = modulos.Any(m => string.Equals(m, "FAQ", StringComparison.OrdinalIgnoreCase));
            var temAgendamento = modulos.Any(m =>
                string.Equals(m, "Reserva", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m, "Agendamentos", StringComparison.OrdinalIgnoreCase));

            var tools = new List<object>();

            if (temServicos)
            {
                tools.AddRange(new object[]
                {
                    new {
                        type = "function",
                        function = new {
                            name = "listar_servicos",
                            description = "Lista os serviços do catálogo do estabelecimento.",
                            parameters = new {
                                type = "object",
                                properties = new {
                                    idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                                    filtro = new { type = "string", description = "Filtro opcional por palavra-chave, como 'freio' ou 'revisão'" }
                                },
                                required = new[] { "idConversa" }
                            }
                        }
                    },
                    new {
                        type = "function",
                        function = new {
                            name = "consultar_servico",
                            description = "Consulta detalhes comerciais de um serviço, incluindo preço, tempo, veículo compatível e marcas de peça.",
                            parameters = new {
                                type = "object",
                                properties = new {
                                    idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                                    nomeServico = new { type = "string", description = "Nome do serviço solicitado" },
                                    veiculo = new { type = "string", description = "Texto completo do veículo, quando disponível" },
                                    veiculoMarca = new { type = "string", description = "Marca do veículo" },
                                    veiculoModelo = new { type = "string", description = "Modelo do veículo" },
                                    marcaPeca = new { type = "string", description = "Marca da peça mencionada ou escolhida" }
                                },
                                required = new[] { "idConversa", "nomeServico" }
                            }
                        }
                    },
                    new {
                        type = "function",
                        function = new {
                            name = "registrar_interesse_servico",
                            description = "Registra o interesse do cliente em um serviço quando as opções relevantes já estiverem definidas.",
                            parameters = new {
                                type = "object",
                                properties = new {
                                    idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                                    nomeCliente = new { type = "string", description = "Nome do cliente" },
                                    nomeServico = new { type = "string", description = "Nome do serviço" },
                                    veiculo = new { type = "string", description = "Texto completo do veículo" },
                                    veiculoMarca = new { type = "string", description = "Marca do veículo" },
                                    veiculoModelo = new { type = "string", description = "Modelo do veículo" },
                                    marcaPeca = new { type = "string", description = "Marca da peça escolhida, quando aplicável" },
                                    observacoes = new { type = "string", description = "Informações adicionais" }
                                },
                                required = new[] { "idConversa", "nomeCliente", "nomeServico" }
                            }
                        }
                    }
                });
            }

            if (temFaq)
            {
                tools.Add(new
                {
                    type = "function",
                    function = new
                    {
                        name = "consultar_faq",
                        description = "Consulta a resposta cadastrada no FAQ do estabelecimento. Use quando o cliente fizer uma pergunta objetiva, inclusive no meio de outro fluxo.",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                                pergunta = new { type = "string", description = "Pergunta do cliente em linguagem natural" }
                            },
                            required = new[] { "idConversa", "pergunta" }
                        }
                    }
                });
            }

            if (temAgendamento)
            {
                tools.AddRange(new object[]
                {
                    new {
                        type = "function",
                        function = new {
                            name = "listar_reservas",
                            description = "Lista todas as reservas ativas do cliente vinculadas ao seu telefone.",
                            parameters = new {
                                type = "object",
                                properties = new {
                                    idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } }
                                },
                                required = new[] { "idConversa" }
                            }
                        }
                    },
                    new {
                        type = "function",
                        function = new {
                            name = "atualizar_reserva",
                            description = "Atualiza uma reserva existente.",
                            parameters = new {
                                type = "object",
                                properties = new {
                                    idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                                    codigoReserva = new { type = "integer", description = "Código da reserva" },
                                    filtroData = new { type = "string", description = "Filtro textual usado para identificar a reserva" },
                                    novoHorario = new { type = "string", description = "Novo horário no formato HH:mm" },
                                    novaQtdPessoas = new { type = "integer", description = "Nova quantidade de pessoas" },
                                    ehMudancaRelativa = new { type = "boolean", description = "Indica se a mudança de pessoas foi relativa" }
                                },
                                required = new[] { "idConversa" }
                            }
                        }
                    },
                    new {
                        type = "function",
                        function = new {
                            name = "confirmar_reserva",
                            description = "Cria uma nova reserva quando o cliente confirmar explicitamente.",
                            parameters = new {
                                type = "object",
                                properties = new {
                                    idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                                    nomeCompleto = new { type = "string", description = "Nome completo do cliente" },
                                    qtdPessoas = new { type = "integer", description = "Quantidade de pessoas" },
                                    data = new { type = "string", description = "Data conforme informada pelo cliente" },
                                    hora = new { type = "string", description = "Horário no formato HH:mm" }
                                },
                                required = new[] { "idConversa", "nomeCompleto", "qtdPessoas", "data", "hora" }
                            }
                        }
                    },
                    new {
                        type = "function",
                        function = new {
                            name = "cancelar_reserva",
                            description = "Cancela uma reserva existente.",
                            parameters = new {
                                type = "object",
                                properties = new {
                                    idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                                    codigoReserva = new { type = "integer", description = "Código da reserva" },
                                    motivoCliente = new { type = "string", description = "Motivo informado pelo cliente" }
                                },
                                required = new[] { "idConversa" }
                            }
                        }
                    }
                });
            }

            tools.Add(new
            {
                type = "function",
                function = new
                {
                    name = "escalar_para_humano",
                    description = "Transfere a conversa para um atendente humano. Só executar após confirmação explícita do cliente.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                            motivo = new { type = "string", description = "Breve explicação do motivo do escalonamento" },
                            resumoConversa = new { type = "string", description = "Resumo do que foi discutido" }
                        },
                        required = new[] { "idConversa", "motivo", "resumoConversa" }
                    }
                }
            });

            return tools.ToArray();
        }

        private static object[] BuildFaqTools(string idConversaString) => new object[]
        {
            new {
                type = "function",
                name = "consultar_faq",
                description = @"Consulta o FAQ cadastrado do estabelecimento.

QUANDO USAR:
- Cliente fizer uma pergunta objetiva sobre funcionamento, regras, endereço, políticas ou dúvidas frequentes
- Cliente interromper outro fluxo com uma pergunta pontual

IMPORTANTE:
- Use este tool para buscar a resposta cadastrada do FAQ
- Responda com base no conteúdo retornado
- Preserve o contexto atual da conversa; FAQ é uma consulta temporária, não um reinício do atendimento.",
                parameters = new {
                    type = "object",
                    properties = new {
                        idConversa = new { type = "string", description = "ID único da conversa atual", @enum = new[] { idConversaString } },
                        pergunta = new { type = "string", description = "Pergunta do cliente em linguagem natural" }
                    },
                    required = new[] { "idConversa", "pergunta" }
                }
            }
        };

        private async Task<string> HandleEscalarParaHumano(EscalarParaHumanoArgs args)
        {
            args.Motivo = args.Motivo?.Trim() ?? string.Empty;
            args.ResumoConversa = args.ResumoConversa?.Trim() ?? string.Empty;

            var controle = await _conversationRepository.ObterControleConversaAsync(args.IdConversa);
            if (JaEscaladoSemAgente(controle))
            {
                return BuildJsonReply(
                    "Seu atendimento já foi encaminhado para a equipe e está aguardando alguém assumir por aqui. Se quiser tratar outro assunto enquanto isso, pode me mandar por aqui.");
            }

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
                return BuildToolDataReply("listar_servicos", "error", "Não consegui identificar o estabelecimento.");

            var catalogo = await _catalogProvider.ObterCatalogoVisivelAsync(scope.IdEstabelecimento);
            _logger.FlowInfo("SERVICO_SEARCH", conversationId: args.IdConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, acao: "listar_servicos",
                extra: new[] { ("filtro", (object?)args.Filtro), ("totalCatalogo", catalogo.Count) });
            if (catalogo.Count == 0)
            {
                _logger.FlowWarning("SERVICO_NOT_FOUND", conversationId: args.IdConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, resultado: "empty", motivo: "catalogo_vazio");
                return BuildToolDataReply("listar_servicos", "empty", "Não encontrei serviços cadastrados no momento.");
            }

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
            {
                _logger.FlowWarning("SERVICO_NOT_FOUND", conversationId: args.IdConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, resultado: "not_found",
                    extra: new[] { ("filtro", (object?)args.Filtro) });
                return BuildToolDataReply("listar_servicos", "not_found", $"Não encontrei serviços relacionados a \"{args.Filtro}\".");
            }

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
            return BuildToolDataReply(
                "listar_servicos",
                "ok",
                sb.ToString(),
                data: new
                {
                    filtro = args.Filtro,
                    servicos = itens.Select(s => new
                    {
                        s.Id,
                        s.Nome,
                        s.Tipo,
                        s.DiferePorVeiculo,
                        s.PermiteAgendamento,
                        valor_centavos = s.ValorCentavos,
                        valor_minimo_centavos = s.ValorMinimoCentavos,
                        valor_maximo_centavos = s.ValorMaximoCentavos
                    }).ToArray()
                },
                fichaAtualSugerida: new ConversationFichaAtual
                {
                    ModuloEmFoco = "servicos"
                });
        }

        private async Task<string> HandleConsultarServico(ConsultarServicoArgs args)
        {
            var scope = await _centralRouting.ResolveEffectiveScopeAsync(args.IdConversa);
            if (scope == null)
                return BuildToolDataReply("consultar_servico", "error", "Não consegui identificar o estabelecimento.");

            var catalogo = await _catalogProvider.ObterCatalogoVisivelAsync(scope.IdEstabelecimento);
            var nomeNorm = args.NomeServico.Trim().ToLowerInvariant();
            _logger.FlowInfo("SERVICO_SEARCH", conversationId: args.IdConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, acao: "consultar_servico",
                extra: new[] { ("termo", (object?)args.NomeServico), ("totalCatalogo", catalogo.Count) });

            var servico = catalogo.FirstOrDefault(s =>
                s.Nome.ToLowerInvariant().Contains(nomeNorm) ||
                s.PalavrasChave.Any(k =>
                    k.ToLowerInvariant().Contains(nomeNorm) ||
                    nomeNorm.Contains(k.ToLowerInvariant())));

            if (servico == null)
            {
                _logger.FlowWarning("SERVICO_NOT_FOUND", conversationId: args.IdConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, resultado: "not_found",
                    extra: new[] { ("termo", (object?)args.NomeServico) });
                return BuildToolDataReply("consultar_servico", "not_found", $"Não encontrei o serviço \"{args.NomeServico}\". Quer ver a lista completa?");
            }

            _logger.FlowInfo("SERVICO_SELECTED", conversationId: args.IdConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, resultado: "ok",
                extra: new[] { ("servicoId", (object?)servico.Id), ("nome", servico.Nome), ("permiteAgendamento", servico.PermiteAgendamento), ("diferePorVeiculo", servico.DiferePorVeiculo) });

            var contexto = await _conversationRepository.ObterContextoAsync(args.IdConversa);
            var fichaPersistida = ConversationFichaAtualStore.Read(contexto);
            var atendimentoAtual = await _servicoAtendimentoRepository.ObterPorConversaAsync(args.IdConversa);

            var veiculoMarca = PrimeiroTextoNaoVazio(
                args.VeiculoMarca,
                fichaPersistida?.VeiculoMarca,
                ObterTexto(atendimentoAtual?.DadosExtras, "veiculo_marca", "marca_veiculo"));
            var veiculoModelo = PrimeiroTextoNaoVazio(
                args.VeiculoModelo,
                fichaPersistida?.VeiculoModelo,
                ObterTexto(atendimentoAtual?.DadosExtras, "veiculo_modelo", "modelo_veiculo"));
            var veiculoTexto = PrimeiroTextoNaoVazio(
                args.Veiculo,
                ObterTexto(atendimentoAtual?.DadosExtras, "veiculo", "servicos_veiculo_nome"),
                MontarVeiculo(veiculoMarca, veiculoModelo));
            var marcaPeca = PrimeiroTextoNaoVazio(
                args.MarcaPeca,
                fichaPersistida?.MarcaPeca,
                ObterTexto(atendimentoAtual?.DadosExtras, "marca_peca", "marcaPeca", "servicos_marca_nome"));

            if ((string.IsNullOrWhiteSpace(veiculoMarca) || string.IsNullOrWhiteSpace(veiculoModelo)) &&
                !string.IsNullOrWhiteSpace(veiculoTexto))
            {
                DecomporVeiculo(veiculoTexto, out var marcaInferida, out var modeloInferido);
                veiculoMarca ??= marcaInferida;
                veiculoModelo ??= modeloInferido;
            }

            var fichaBase = new ConversationFichaAtual
            {
                ModuloEmFoco = "servicos",
                Servico = servico.Nome,
                VeiculoMarca = veiculoMarca,
                VeiculoModelo = veiculoModelo,
                MarcaPeca = marcaPeca,
                ProntoParaAgendamento = false
            };

            if (servico.DiferePorVeiculo &&
                (string.IsNullOrWhiteSpace(veiculoMarca) || string.IsNullOrWhiteSpace(veiculoModelo)))
            {
                fichaBase.Pendencias = new List<string> { "veiculo_marca", "veiculo_modelo" };
                return BuildToolDataReply(
                    "consultar_servico",
                    "needs_vehicle",
                    $"Já localizei o serviço {servico.Nome}, mas para continuar eu preciso da marca e do modelo do veículo.",
                    data: new
                    {
                        servico = new
                        {
                            servico.Id,
                            servico.Nome,
                            servico.Tipo,
                            servico.DiferePorVeiculo,
                            servico.PermiteAgendamento,
                            duracao_minutos = servico.DuracaoMinutos
                        }
                    },
                    fichaAtualSugerida: fichaBase);
            }

            var veiculoSelecionado = servico.DiferePorVeiculo
                ? ResolverVeiculo(servico, veiculoMarca, veiculoModelo, veiculoTexto)
                : null;

            if (servico.DiferePorVeiculo && veiculoSelecionado == null)
            {
                fichaBase.Pendencias = new List<string> { "veiculo_marca", "veiculo_modelo" };
                return BuildToolDataReply(
                    "consultar_servico",
                    "vehicle_not_found",
                    $"Encontrei o serviço {servico.Nome}, mas não achei esse veículo no catálogo. Pode me confirmar a marca e o modelo exatamente como estão no carro?",
                    data: new
                    {
                        servico = new
                        {
                            servico.Id,
                            servico.Nome,
                            servico.Tipo,
                            servico.DiferePorVeiculo,
                            servico.PermiteAgendamento
                        },
                        veiculo_informado = MontarVeiculo(veiculoMarca, veiculoModelo) ?? veiculoTexto
                    },
                    fichaAtualSugerida: fichaBase);
            }

            var marcaSelecionada = veiculoSelecionado == null ? null : ResolverMarcaPeca(veiculoSelecionado, marcaPeca);
            var pendencias = new List<string>();
            if (veiculoSelecionado != null &&
                veiculoSelecionado.MarcasPeca.Count > 0 &&
                marcaSelecionada == null &&
                string.IsNullOrWhiteSpace(marcaPeca))
            {
                pendencias.Add("marca_peca");
            }

            var valorCentavos = marcaSelecionada?.ValorCentavos
                ?? veiculoSelecionado?.ValorCentavos
                ?? servico.ValorCentavos;
            var valorMinimoCentavos = marcaSelecionada?.ValorMinimoCentavos
                ?? veiculoSelecionado?.ValorMinimoCentavos
                ?? servico.ValorMinimoCentavos;
            var valorMaximoCentavos = marcaSelecionada?.ValorMaximoCentavos
                ?? veiculoSelecionado?.ValorMaximoCentavos
                ?? servico.ValorMaximoCentavos;

            var sb = new StringBuilder();
            sb.AppendLine($"🔧 *{servico.Nome}*");

            if (!string.IsNullOrWhiteSpace(servico.Descricao))
                sb.AppendLine(servico.Descricao);

            if (veiculoSelecionado != null)
            {
                sb.AppendLine($"\n🚗 Veículo: {veiculoSelecionado.NomeExibicao}");
            }

            if (valorCentavos.HasValue)
            {
                sb.AppendLine($"\n💰 Valor: R$ {valorCentavos.Value / 100m:N2}");
            }
            else if (valorMinimoCentavos.HasValue && valorMaximoCentavos.HasValue)
            {
                sb.AppendLine($"\n💰 Valor: R$ {valorMinimoCentavos.Value / 100m:N2} a R$ {valorMaximoCentavos.Value / 100m:N2}");
            }
            else if (valorMinimoCentavos.HasValue)
            {
                sb.AppendLine($"\n💰 Valor: a partir de R$ {valorMinimoCentavos.Value / 100m:N2}");
            }
            else
            {
                sb.AppendLine("\n💰 O catálogo não tem valor cadastrado para esse cenário.");
            }

            if (servico.DuracaoMinutos > 0)
                sb.AppendLine($"⏱ Tempo médio: {servico.DuracaoMinutos} min");

            if (veiculoSelecionado != null && veiculoSelecionado.MarcasPeca.Count > 0)
            {
                sb.AppendLine($"\n🧩 Marcas disponíveis: {string.Join(", ", veiculoSelecionado.MarcasPeca.Select(item => item.Nome))}");
                if (marcaSelecionada != null)
                {
                    sb.AppendLine($"Marca selecionada: {marcaSelecionada.Nome}");
                }
            }

            if (pendencias.Contains("marca_peca"))
            {
                sb.AppendLine("\nSe quiser seguir, me diga qual marca você prefere.");
            }
            else if (servico.PermiteAgendamento && (!servico.DiferePorVeiculo || veiculoSelecionado != null))
            {
                sb.AppendLine("\nSe estiver tudo certo, eu posso deixar seu atendimento pronto para seguir para agendamento.");
            }

            fichaBase.MarcaPeca = marcaSelecionada?.Nome ?? marcaPeca;
            fichaBase.Pendencias = pendencias;
            fichaBase.ProntoParaAgendamento = servico.PermiteAgendamento &&
                                              !pendencias.Any() &&
                                              (!servico.DiferePorVeiculo || veiculoSelecionado != null);

            return BuildToolDataReply(
                "consultar_servico",
                "ok",
                sb.ToString(),
                data: new
                {
                    servico = new
                    {
                        servico.Id,
                        servico.Nome,
                        servico.Tipo,
                        servico.Descricao,
                        duracao_minutos = servico.DuracaoMinutos,
                        servico.PermiteAgendamento,
                        servico.DiferePorVeiculo
                    },
                    veiculo = veiculoSelecionado == null ? null : new
                    {
                        nome = veiculoSelecionado.NomeExibicao,
                        valor_centavos = veiculoSelecionado.ValorCentavos,
                        valor_minimo_centavos = veiculoSelecionado.ValorMinimoCentavos,
                        valor_maximo_centavos = veiculoSelecionado.ValorMaximoCentavos
                    },
                    marca_peca = marcaSelecionada == null ? null : new
                    {
                        marcaSelecionada.Id,
                        marcaSelecionada.Nome,
                        valor_centavos = marcaSelecionada.ValorCentavos,
                        valor_minimo_centavos = marcaSelecionada.ValorMinimoCentavos,
                        valor_maximo_centavos = marcaSelecionada.ValorMaximoCentavos
                    },
                    marcas_disponiveis = veiculoSelecionado == null
                        ? null
                        : veiculoSelecionado.MarcasPeca.Select(item => new
                        {
                            item.Id,
                            item.Nome,
                            valor_centavos = item.ValorCentavos,
                            valor_minimo_centavos = item.ValorMinimoCentavos,
                            valor_maximo_centavos = item.ValorMaximoCentavos
                        }).ToArray(),
                    valor_centavos = valorCentavos,
                    valor_minimo_centavos = valorMinimoCentavos,
                    valor_maximo_centavos = valorMaximoCentavos
                },
                fichaAtualSugerida: fichaBase);
        }

        private async Task<string> HandleConsultarFaq(ConsultarFaqArgs args)
        {
            var scope = await _centralRouting.ResolveEffectiveScopeAsync(args.IdConversa);
            if (scope == null)
            {
                return BuildToolDataReply("consultar_faq", "error", "Não consegui identificar o estabelecimento.");
            }

            var pergunta = args.Pergunta?.Trim() ?? string.Empty;
            _logger.FlowInfo("FAQ_SEARCH", conversationId: args.IdConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, acao: "consultar_faq",
                extra: new[] { ("pergunta", (object?)pergunta) });
            if (string.IsNullOrWhiteSpace(pergunta))
            {
                return BuildToolDataReply("consultar_faq", "invalid", "Preciso da pergunta do cliente para consultar o FAQ.");
            }

            var match = await _faqCatalogProvider.MatchStrongAsync(scope.IdEstabelecimento, pergunta);
            if (match == null)
            {
                _logger.FlowInfo("FAQ_NOT_FOUND", conversationId: args.IdConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, resultado: "not_found",
                    extra: new[] { ("pergunta", (object?)pergunta) });
                return BuildToolDataReply(
                    "consultar_faq",
                    "not_found",
                    "Não encontrei uma resposta forte no FAQ para essa pergunta.",
                    data: new
                    {
                        pergunta
                    });
            }

            _logger.FlowInfo("FAQ_ANSWERED", conversationId: args.IdConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, resultado: "ok",
                extra: new[] { ("faqId", (object?)match.Item.Id), ("categoria", match.Item.Categoria), ("matchScore", match.Score) });

            return BuildToolDataReply(
                "consultar_faq",
                "ok",
                match.Item.Resposta.Trim(),
                data: new
                {
                    faq = new
                    {
                        match.Item.Id,
                        pergunta = match.Item.Pergunta,
                        resposta = match.Item.Resposta,
                        categoria = match.Item.Categoria,
                        palavras_chave = match.Item.PalavrasChave
                    },
                    match_score = match.Score,
                    contexto_preservado = true
                });
        }

        private async Task<string> HandleConsultarDisponibilidadeOficina(ConsultarDisponibilidadeOficinaArgs args)
        {
            var scope = await ResolveOficinaScopeAsync(args.IdConversa, "consultar_disponibilidade_oficina");
            if (scope == null)
                return BuildToolDataReply("consultar_disponibilidade_oficina", "error", "Não consegui identificar seus dados para consultar a agenda.");

            if (!TryParseDate(args.Data, out var data))
                return BuildToolDataReply("consultar_disponibilidade_oficina", "invalid", "Preciso de uma data válida para consultar horários.");

            var servico = await ResolverServicoCatalogoAsync(scope.IdEstabelecimento, args.NomeServico);
            if (servico == null)
            {
                _logger.FlowWarning("SERVICO_NOT_FOUND", conversationId: args.IdConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, resultado: "not_found",
                    extra: new[] { ("nomeServico", (object?)args.NomeServico) });
                return BuildToolDataReply("consultar_disponibilidade_oficina", "service_not_found", $"Não encontrei o serviço \"{args.NomeServico}\" no catálogo.");
            }

            if (!servico.PermiteAgendamento)
            {
                _logger.FlowWarning("AGENDA_SLOT_EMPTY", conversationId: args.IdConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, motivo: "servico_nao_agendavel",
                    extra: new[] { ("servicoId", (object?)servico.Id), ("nomeServico", servico.Nome) });
                return BuildToolDataReply("consultar_disponibilidade_oficina", "service_not_schedulable", $"O serviço {servico.Nome} não está liberado para agendamento automático. Posso chamar a equipe para continuar.");
            }

            var duracao = args.DuracaoMinutos ?? servico.DuracaoMinutos;
            var slots = await _oficinaAgendamentoService.BuscarSlotsAsync(scope.IdEstabelecimento, servico.Id, data, duracao, args.ProfissionalId);
            if (slots.Count == 0)
            {
                return BuildToolDataReply("consultar_disponibilidade_oficina", "empty", "Não encontrei horários disponíveis nessa data. Quer tentar outro dia?");
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Encontrei estes horários para *{servico.Nome}* em {data:dd/MM/yyyy}:");
            sb.AppendLine();
            foreach (var slot in slots.Take(6))
            {
                sb.AppendLine($"- {slot.HoraInicio}");
            }

            sb.AppendLine();
            sb.Append("Qual horário você prefere?");

            return BuildToolDataReply(
                "consultar_disponibilidade_oficina",
                "ok",
                sb.ToString(),
                data: new
                {
                    servico = new { servico.Id, servico.Nome, duracao_minutos = duracao },
                    data = data.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    slots
                },
                fichaAtualSugerida: new ConversationFichaAtual
                {
                    ModuloEmFoco = "servicos",
                    Servico = servico.Nome,
                    ProntoParaAgendamento = true
                });
        }

        private async Task<string> HandleCriarAgendamentoOficina(CriarAgendamentoOficinaArgs args)
        {
            var scope = await ResolveOficinaScopeAsync(args.IdConversa, "criar_agendamento_oficina");
            if (scope == null)
                return BuildToolDataReply("criar_agendamento_oficina", "error", "Não consegui identificar seus dados para criar o agendamento.");

            if (!TryParseDate(args.Data, out var data))
                return BuildToolDataReply("criar_agendamento_oficina", "invalid", "Preciso de uma data válida para criar o agendamento.");

            var servico = await ResolverServicoCatalogoAsync(scope.IdEstabelecimento, args.NomeServico);
            if (servico == null)
                return BuildToolDataReply("criar_agendamento_oficina", "service_not_found", $"Não encontrei o serviço \"{args.NomeServico}\" no catálogo.");

            if (!servico.PermiteAgendamento)
                return BuildToolDataReply("criar_agendamento_oficina", "service_not_schedulable", $"O serviço {servico.Nome} não está liberado para agendamento automático. Posso chamar a equipe para continuar.");

            var atendimento = await _servicoAtendimentoRepository.ObterPorConversaAsync(args.IdConversa);
            var veiculoTexto = PrimeiroTextoNaoVazio(args.Veiculo, MontarVeiculo(args.VeiculoMarca, args.VeiculoModelo), ObterTexto(atendimento?.DadosExtras, "veiculo"));
            var marcaPeca = PrimeiroTextoNaoVazio(args.MarcaPeca, ObterTexto(atendimento?.DadosExtras, "marca_peca"));
            var veiculoMarca = PrimeiroTextoNaoVazio(args.VeiculoMarca, ObterTexto(atendimento?.DadosExtras, "veiculo_marca"));
            var veiculoModelo = PrimeiroTextoNaoVazio(args.VeiculoModelo, ObterTexto(atendimento?.DadosExtras, "veiculo_modelo"));
            if ((string.IsNullOrWhiteSpace(veiculoMarca) || string.IsNullOrWhiteSpace(veiculoModelo)) && !string.IsNullOrWhiteSpace(veiculoTexto))
            {
                DecomporVeiculo(veiculoTexto, out var marcaInferida, out var modeloInferido);
                veiculoMarca ??= marcaInferida;
                veiculoModelo ??= modeloInferido;
            }

            _logger.FlowInfo("AGENDAMENTO_CREATE_ATTEMPT", conversationId: args.IdConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, acao: "criar_agendamento",
                extra: new[] { ("servicoId", (object?)servico.Id), ("data", data.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("hora", args.HoraInicio) });

            try
            {
                var agendamento = await _oficinaAgendamentoService.CriarAsync(
                    scope.IdEstabelecimento,
                    scope.IdCliente,
                    scope.TelefoneCliente ?? string.Empty,
                    new CriarOficinaAgendamentoRequest
                    {
                        ConversaId = args.IdConversa,
                        AtendimentoServicoId = atendimento?.Id,
                        ServicoId = servico.Id,
                        ProfissionalId = args.ProfissionalId,
                        NomeCliente = PrimeiroTextoNaoVazio(args.NomeCliente, atendimento?.NomeCliente),
                        NomeServico = servico.Nome,
                        VeiculoMarca = veiculoMarca,
                        VeiculoModelo = veiculoModelo,
                        MarcaPeca = marcaPeca,
                        DataAgendamento = data,
                        HoraInicio = args.HoraInicio,
                        DuracaoMinutos = args.DuracaoMinutos ?? servico.DuracaoMinutos,
                        Observacao = args.Observacoes,
                        DadosExtras = new Dictionary<string, object?>
                        {
                            ["veiculo"] = veiculoTexto,
                            ["origem"] = "whatsapp_bot"
                        }
                    });

                if (atendimento != null)
                {
                    atendimento.Status = "agendado";
                    atendimento.EtapaAtual = "agendado";
                    atendimento.IntencaoPrincipal = "agendamento";
                    atendimento.IntencaoDetalhe = "agendamento_confirmado";
                    atendimento.IdServico = servico.Id;
                    atendimento.NomeServico = servico.Nome;
                    atendimento.DadosExtras["oficina_agendamento_id"] = agendamento.Id;
                    atendimento.DadosExtras["oficina_agendamento_codigo"] = agendamento.Codigo;
                    atendimento.DadosExtras["data_agendamento"] = agendamento.DataAgendamento.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    atendimento.DadosExtras["hora_agendamento"] = agendamento.HoraInicio;
                    atendimento.DataAtualizacao = DateTime.UtcNow;
                    await _servicoAtendimentoRepository.AtualizarAsync(atendimento);
                    _logger.FlowInfo("ATENDIMENTO_STATUS_UPDATED", conversationId: args.IdConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, estadoNovo: "agendado", resultado: "ok",
                        extra: new[] { ("atendimentoId", (object?)atendimento.Id), ("agendamentoId", agendamento.Id) });
                }

                var reply = new StringBuilder();
                reply.AppendLine("✅ Agendamento confirmado!");
                reply.AppendLine();
                reply.AppendLine($"Serviço: {agendamento.NomeServico}");
                reply.AppendLine($"Data: {agendamento.DataAgendamento:dd/MM/yyyy}");
                reply.AppendLine($"Horário: {agendamento.HoraInicio}");
                if (!string.IsNullOrWhiteSpace(veiculoTexto))
                    reply.AppendLine($"Veículo: {veiculoTexto}");
                reply.AppendLine($"Código: {agendamento.Codigo}");
                reply.AppendLine();
                reply.Append("Se precisar remarcar ou cancelar, é só me informar esse código.");

                return BuildToolDataReply("criar_agendamento_oficina", "created", reply.ToString(), data: new { agendamento });
            }
            catch (Exception ex)
            {
                _logger.FlowError(ex, "AGENDAMENTO_CREATE_FAILED", conversationId: args.IdConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, resultado: "failed", motivo: ex.Message);
                return BuildToolDataReply("criar_agendamento_oficina", "error", ex.Message);
            }
        }

        private async Task<string> HandleListarAgendamentosOficina(Guid idConversa)
        {
            var scope = await ResolveOficinaScopeAsync(idConversa, "listar_agendamentos_oficina");
            if (scope == null)
                return BuildToolDataReply("listar_agendamentos_oficina", "error", "Não consegui identificar seus dados para buscar agendamentos.");

            _logger.FlowInfo("AGENDAMENTO_LOOKUP", conversationId: idConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, acao: "listar_ativos");
            var agendamentos = (await _oficinaAgendamentoService.ListarAtivosPorClienteAsync(scope.IdEstabelecimento, scope.IdCliente, scope.TelefoneCliente)).ToList();
            if (agendamentos.Count == 0)
            {
                return BuildToolDataReply("listar_agendamentos_oficina", "empty", "Não encontrei agendamentos ativos no seu nome.");
            }

            var sb = new StringBuilder();
            sb.AppendLine(agendamentos.Count == 1 ? "Encontrei este agendamento:" : $"Encontrei {agendamentos.Count} agendamentos ativos:");
            sb.AppendLine();
            foreach (var item in agendamentos)
            {
                sb.AppendLine($"Código: {item.Codigo}");
                sb.AppendLine($"Serviço: {item.NomeServico}");
                sb.AppendLine($"Data: {item.DataAgendamento:dd/MM/yyyy}");
                sb.AppendLine($"Horário: {item.HoraInicio}");
                sb.AppendLine($"Status: {item.Status}");
                sb.AppendLine();
            }

            _logger.FlowInfo("AGENDAMENTO_STATUS_FOUND", conversationId: idConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, resultado: "ok",
                extra: new[] { ("total", (object?)agendamentos.Count) });
            return BuildToolDataReply("listar_agendamentos_oficina", "ok", sb.ToString().Trim(), data: new { agendamentos });
        }

        private async Task<string> HandleCancelarAgendamentoOficina(CodigoAgendamentoOficinaArgs args)
        {
            var scope = await ResolveOficinaScopeAsync(args.IdConversa, "cancelar_agendamento_oficina");
            if (scope == null)
                return BuildToolDataReply("cancelar_agendamento_oficina", "error", "Não consegui identificar seus dados para cancelar agendamento.");

            var agendamento = await ResolverAgendamentoDoClienteAsync(scope.IdEstabelecimento, scope.IdCliente, scope.TelefoneCliente, args.CodigoAgendamento);
            if (agendamento == null)
                return BuildToolDataReply("cancelar_agendamento_oficina", "not_found", "Não encontrei um agendamento ativo para cancelar.");

            _logger.FlowInfo("AGENDAMENTO_CANCEL_REQUEST", conversationId: args.IdConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, acao: "cancelar",
                extra: new[] { ("agendamentoId", (object?)agendamento.Id), ("codigo", agendamento.Codigo) });
            var cancelado = await _oficinaAgendamentoService.CancelarAsync(scope.IdEstabelecimento, agendamento.Id, args.MotivoCliente);

            return BuildToolDataReply(
                "cancelar_agendamento_oficina",
                "cancelled",
                $"✅ Agendamento {cancelado.Codigo} cancelado com sucesso.",
                data: new { agendamento = cancelado });
        }

        private async Task<string> HandleRemarcarAgendamentoOficina(RemarcarAgendamentoOficinaArgs args)
        {
            var scope = await ResolveOficinaScopeAsync(args.IdConversa, "remarcar_agendamento_oficina");
            if (scope == null)
                return BuildToolDataReply("remarcar_agendamento_oficina", "error", "Não consegui identificar seus dados para remarcar agendamento.");

            if (!TryParseDate(args.NovaData, out var novaData))
                return BuildToolDataReply("remarcar_agendamento_oficina", "invalid", "Preciso de uma nova data válida para remarcar.");

            var agendamento = await ResolverAgendamentoDoClienteAsync(scope.IdEstabelecimento, scope.IdCliente, scope.TelefoneCliente, args.CodigoAgendamento);
            if (agendamento == null)
                return BuildToolDataReply("remarcar_agendamento_oficina", "not_found", "Não encontrei um agendamento ativo para remarcar.");

            _logger.FlowInfo("AGENDAMENTO_CHANGE_ATTEMPT", conversationId: args.IdConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, acao: "remarcar",
                extra: new[] { ("agendamentoId", (object?)agendamento.Id), ("novaData", novaData.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("novoHorario", args.NovoHorario) });

            try
            {
                var remarcado = await _oficinaAgendamentoService.RemarcarAsync(
                    scope.IdEstabelecimento,
                    agendamento.Id,
                    new RemarcarOficinaAgendamentoRequest
                    {
                        DataAgendamento = novaData,
                        HoraInicio = args.NovoHorario,
                        DuracaoMinutos = args.DuracaoMinutos,
                        ProfissionalId = args.ProfissionalId
                    });

                return BuildToolDataReply(
                    "remarcar_agendamento_oficina",
                    "changed",
                    $"✅ Agendamento {remarcado.Codigo} remarcado para {remarcado.DataAgendamento:dd/MM/yyyy} às {remarcado.HoraInicio}.",
                    data: new { agendamento = remarcado });
            }
            catch (Exception ex)
            {
                _logger.FlowError(ex, "AGENDAMENTO_OPERATION_FAILED", conversationId: args.IdConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, resultado: "failed", motivo: ex.Message);
                return BuildToolDataReply("remarcar_agendamento_oficina", "error", ex.Message);
            }
        }

        private async Task<string> HandleRegistrarInteresseServico(RegistrarInteresseServicoArgs args)
        {
            var scope = await _centralRouting.ResolveEffectiveScopeAsync(args.IdConversa);
            if (scope == null)
                return BuildToolDataReply("registrar_interesse_servico", "error", "Não consegui identificar o estabelecimento.");

            var veiculoTexto = PrimeiroTextoNaoVazio(args.Veiculo, MontarVeiculo(args.VeiculoMarca, args.VeiculoModelo));
            var marcaPeca = PrimeiroTextoNaoVazio(args.MarcaPeca);
            var controleConversa = await _conversationRepository.ObterControleConversaAsync(args.IdConversa);
            var atendimentoAberto = string.IsNullOrWhiteSpace(scope.TelefoneCliente)
                ? null
                : await _servicoAtendimentoRepository.ObterAbertoAsync(scope.IdEstabelecimento, scope.TelefoneCliente);

            if (atendimentoAberto != null &&
                JaEscaladoSemAgente(controleConversa) &&
                MesmoAssuntoServico(atendimentoAberto, args.NomeServico, veiculoTexto, args.VeiculoMarca, args.VeiculoModelo, marcaPeca))
            {
                _logger.FlowInfo("HANDOVER_SKIPPED_ALREADY_OPEN", conversationId: args.IdConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, resultado: "already_open",
                    extra: new[] { ("atendimentoId", (object?)atendimentoAberto.Id), ("nomeServico", atendimentoAberto.NomeServico) });
                var primeiroNomeExistente = ExtrairPrimeiroNome(args.NomeCliente, atendimentoAberto.NomeCliente);
                var tratamentoExistente = string.IsNullOrWhiteSpace(primeiroNomeExistente)
                    ? "Perfeito!"
                    : $"Perfeito, {primeiroNomeExistente}!";
                var nomeServico = string.IsNullOrWhiteSpace(atendimentoAberto.NomeServico) ? args.NomeServico.Trim() : atendimentoAberto.NomeServico!.Trim();
                var veiculoExistente = PrimeiroTextoNaoVazio(
                    ObterTexto(atendimentoAberto.DadosExtras, "veiculo"),
                    MontarVeiculo(
                        ObterTexto(atendimentoAberto.DadosExtras, "veiculo_marca"),
                        ObterTexto(atendimentoAberto.DadosExtras, "veiculo_modelo")));

                return BuildToolDataReply(
                    "registrar_interesse_servico",
                    "already_escalated",
                    $"{tratamentoExistente} Seu atendimento sobre *{nomeServico}* já foi encaminhado para a equipe e está aguardando alguém assumir por aqui.\n\n" +
                    "Se quiser tratar outro assunto enquanto isso, pode me mandar por aqui.",
                    data: new
                    {
                        atendimentoAberto.Id,
                        nome_cliente = atendimentoAberto.NomeCliente,
                        nome_servico = nomeServico,
                        veiculo = veiculoExistente,
                        marca_peca = ObterTexto(atendimentoAberto.DadosExtras, "marca_peca")
                    },
                    fichaAtualSugerida: new ConversationFichaAtual
                    {
                        NomeCliente = PrimeiroTextoNaoVazio(atendimentoAberto.NomeCliente, args.NomeCliente),
                        ModuloEmFoco = "servicos",
                        Servico = nomeServico,
                        VeiculoMarca = PrimeiroTextoNaoVazio(
                            ObterTexto(atendimentoAberto.DadosExtras, "veiculo_marca"),
                            args.VeiculoMarca),
                        VeiculoModelo = PrimeiroTextoNaoVazio(
                            ObterTexto(atendimentoAberto.DadosExtras, "veiculo_modelo"),
                            args.VeiculoModelo),
                        MarcaPeca = PrimeiroTextoNaoVazio(
                            ObterTexto(atendimentoAberto.DadosExtras, "marca_peca"),
                            marcaPeca),
                        Pendencias = new List<string>(),
                        ProntoParaAgendamento = true
                    });
            }

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
                IntencaoDetalhe = "registrar_interesse_servico",
                ResumoAtendimento = $"Interesse em serviço: {args.NomeServico.Trim()}",
                Status = "aguardando_interno",
                EtapaAtual = "encaminhado",
                DadosExtras = new Dictionary<string, object?>
                {
                    ["veiculo"] = veiculoTexto,
                    ["veiculo_marca"] = args.VeiculoMarca,
                    ["veiculo_modelo"] = args.VeiculoModelo,
                    ["marca_peca"] = marcaPeca,
                    ["observacoes"] = args.Observacoes
                },
                DataCriacao = DateTime.UtcNow,
                DataAtualizacao = DateTime.UtcNow,
                DataHandover = DateTime.UtcNow
            };

            await _servicoAtendimentoRepository.CriarAsync(atendimento);

            var contextoResumo = $"Interesse em: {args.NomeServico}" +
                                  (string.IsNullOrWhiteSpace(veiculoTexto) ? string.Empty : $" | Veículo: {veiculoTexto}") +
                                  (string.IsNullOrWhiteSpace(marcaPeca) ? string.Empty : $" | Marca: {marcaPeca}") +
                                  (string.IsNullOrWhiteSpace(args.Observacoes) ? string.Empty : $" | Obs: {args.Observacoes}");

            var detalhes = new HandoverContextDto
            {
                ClienteNome = args.NomeCliente.Trim(),
                Motivo = "Interesse em serviço",
                Contexto = contextoResumo
            };

            _logger.FlowInfo("HANDOVER_REQUESTED", conversationId: args.IdConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, acao: "registrar_interesse_servico", motivo: "interesse_servico",
                extra: new[] { ("atendimentoId", (object?)atendimento.Id), ("nomeServico", atendimento.NomeServico), ("veiculo", veiculoTexto) });
            await _handoverService.SolicitarAtendimentoHumanoAsync(args.IdConversa, detalhes);
            _logger.FlowInfo("HANDOVER_CREATED", conversationId: args.IdConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, resultado: "ok",
                extra: new[] { ("atendimentoId", (object?)atendimento.Id) });

            var primeiroNome = args.NomeCliente.Trim().Split(' ')[0];
            return BuildToolDataReply(
                "registrar_interesse_servico",
                "registered",
                $"✅ Perfeito, {primeiroNome}! Registrei seu interesse em *{args.NomeServico}*.\n\n" +
                "Nossa equipe vai continuar o atendimento com você a partir daqui. 😊",
                data: new
                {
                    atendimento.Id,
                    nome_cliente = atendimento.NomeCliente,
                    nome_servico = atendimento.NomeServico,
                    veiculo = veiculoTexto,
                    marca_peca = marcaPeca
                },
                fichaAtualSugerida: new ConversationFichaAtual
                {
                    NomeCliente = atendimento.NomeCliente,
                    ModuloEmFoco = "servicos",
                    Servico = atendimento.NomeServico,
                    VeiculoMarca = args.VeiculoMarca,
                    VeiculoModelo = args.VeiculoModelo,
                    MarcaPeca = marcaPeca,
                    Pendencias = new List<string>(),
                    ProntoParaAgendamento = true
                });
        }

        private async Task<EffectiveConversationScope?> ResolveOficinaScopeAsync(Guid idConversa, string acao)
        {
            var scope = await _centralRouting.ResolveEffectiveScopeAsync(idConversa);
            if (scope == null || scope.IdEstabelecimento == Guid.Empty || scope.IdCliente == Guid.Empty)
            {
                _logger.FlowWarning("OFICINA_FLOW_DECISION", conversationId: idConversa, acao: acao, resultado: "failed", motivo: "scope_invalido");
                return null;
            }

            var tipo = await _estabelecimentoRepository.ObterTipoEstabelecimentoAsync(scope.IdEstabelecimento);
            if (!string.Equals(tipo, "oficina", StringComparison.OrdinalIgnoreCase))
            {
                _logger.FlowWarning("OFICINA_FLOW_DECISION", conversationId: idConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, tipoEstabelecimento: tipo, acao: acao, resultado: "ignored", motivo: "nao_e_oficina");
                return null;
            }

            var contexto = await _conversationRepository.ObterContextoAsync(idConversa);
            var modulos = await _estabelecimentoRepository.ObterModulosAtivosAsync(scope.IdEstabelecimento);
            _logger.FlowInfo("OFICINA_FLOW_START", conversationId: idConversa, estabelecimentoId: scope.IdEstabelecimento, clienteId: scope.IdCliente, telefone: scope.TelefoneCliente, tipoEstabelecimento: tipo, modulosAtivos: modulos, estadoAnterior: contexto?.Estado, acao: acao, resultado: "ok");
            return scope;
        }

        private async Task<ServicoCatalogItem?> ResolverServicoCatalogoAsync(Guid idEstabelecimento, string nomeServico)
        {
            var catalogo = await _catalogProvider.ObterCatalogoVisivelAsync(idEstabelecimento);
            var nomeNorm = NormalizarComparacao(nomeServico);
            _logger.FlowInfo("SERVICO_SEARCH", estabelecimentoId: idEstabelecimento, acao: "resolver_servico",
                extra: new[] { ("termo", (object?)nomeServico), ("totalCatalogo", catalogo.Count) });

            if (string.IsNullOrWhiteSpace(nomeNorm))
            {
                return null;
            }

            var servico = catalogo.FirstOrDefault(s => NormalizarComparacao(s.Nome) == nomeNorm)
                ?? catalogo.FirstOrDefault(s => NormalizarComparacao(s.Nome).Contains(nomeNorm) || nomeNorm.Contains(NormalizarComparacao(s.Nome)))
                ?? catalogo.FirstOrDefault(s => s.PalavrasChave.Any(k =>
                {
                    var keyword = NormalizarComparacao(k);
                    return keyword.Contains(nomeNorm) || nomeNorm.Contains(keyword);
                }));

            if (servico == null)
            {
                _logger.FlowWarning("SERVICO_NOT_FOUND", estabelecimentoId: idEstabelecimento, resultado: "not_found", extra: new[] { ("termo", (object?)nomeServico) });
                return null;
            }

            _logger.FlowInfo("SERVICO_SELECTED", estabelecimentoId: idEstabelecimento, resultado: "ok",
                extra: new[] { ("servicoId", (object?)servico.Id), ("nome", servico.Nome), ("permiteAgendamento", servico.PermiteAgendamento), ("diferePorVeiculo", servico.DiferePorVeiculo) });
            return servico;
        }

        private async Task<OficinaAgendamentoDto?> ResolverAgendamentoDoClienteAsync(Guid idEstabelecimento, Guid idCliente, string? telefoneE164, string? codigoAgendamento)
        {
            if (!string.IsNullOrWhiteSpace(codigoAgendamento))
            {
                var porCodigo = await _oficinaAgendamentoService.ObterPorCodigoAsync(idEstabelecimento, codigoAgendamento.Trim());
                if (porCodigo != null && (porCodigo.ClienteId == idCliente || string.Equals(porCodigo.TelefoneE164, telefoneE164, StringComparison.OrdinalIgnoreCase)))
                {
                    return porCodigo;
                }
            }

            var ativos = (await _oficinaAgendamentoService.ListarAtivosPorClienteAsync(idEstabelecimento, idCliente, telefoneE164)).OrderBy(item => item.DataAgendamento).ThenBy(item => item.HoraInicio).ToList();
            return ativos.Count == 1 ? ativos[0] : null;
        }

        private static bool TryParseDate(string? value, out DateTime data)
        {
            value = value?.Trim() ?? string.Empty;
            return DateTime.TryParseExact(value, new[] { "yyyy-MM-dd", "dd/MM/yyyy", "dd/MM" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out data) ||
                   DateTime.TryParse(value, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.AssumeLocal, out data);
        }

        private static string? PrimeiroTextoNaoVazio(params string?[] valores)
        {
            foreach (var valor in valores)
            {
                if (!string.IsNullOrWhiteSpace(valor))
                {
                    return valor.Trim();
                }
            }

            return null;
        }

        private static string? MontarVeiculo(string? marca, string? modelo)
        {
            var partes = new[] { marca?.Trim(), modelo?.Trim() }
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();

            return partes.Length == 0 ? null : string.Join(" ", partes);
        }

        private static bool JaEscaladoSemAgente(ConversationControlDto? controle)
        {
            if (controle == null || controle.AssignedAgentId.HasValue)
            {
                return false;
            }

            return string.Equals(controle.Status, "aguardando_interno", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(controle.Status, "em_andamento", StringComparison.OrdinalIgnoreCase);
        }

        private static bool MesmoAssuntoServico(
            ServicoAtendimento atendimento,
            string? nomeServico,
            string? veiculoTexto,
            string? veiculoMarca,
            string? veiculoModelo,
            string? marcaPeca)
        {
            if (!MesmoTexto(atendimento.NomeServico, nomeServico))
            {
                return false;
            }

            var veiculoAtual = PrimeiroTextoNaoVazio(
                ObterTexto(atendimento.DadosExtras, "veiculo"),
                MontarVeiculo(
                    ObterTexto(atendimento.DadosExtras, "veiculo_marca"),
                    ObterTexto(atendimento.DadosExtras, "veiculo_modelo")));

            if (!MesmoTextoOpcional(veiculoAtual, veiculoTexto))
            {
                return false;
            }

            if (!MesmoTextoOpcional(ObterTexto(atendimento.DadosExtras, "veiculo_marca"), veiculoMarca))
            {
                return false;
            }

            if (!MesmoTextoOpcional(ObterTexto(atendimento.DadosExtras, "veiculo_modelo"), veiculoModelo))
            {
                return false;
            }

            return MesmoTextoOpcional(ObterTexto(atendimento.DadosExtras, "marca_peca"), marcaPeca);
        }

        private static bool MesmoTexto(string? atual, string? recebido)
            => string.Equals(NormalizarComparacao(atual), NormalizarComparacao(recebido), StringComparison.Ordinal);

        private static bool MesmoTextoOpcional(string? atual, string? recebido)
        {
            if (string.IsNullOrWhiteSpace(atual) || string.IsNullOrWhiteSpace(recebido))
            {
                return true;
            }

            return MesmoTexto(atual, recebido);
        }

        private static string NormalizarComparacao(string? texto)
        {
            if (string.IsNullOrWhiteSpace(texto))
            {
                return string.Empty;
            }

            var normalized = texto.Normalize(NormalizationForm.FormD);
            var chars = normalized
                .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                .ToArray();

            return string.Join(" ",
                new string(chars)
                    .ToLowerInvariant()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        private static string ExtrairPrimeiroNome(params string?[] nomes)
        {
            foreach (var nome in nomes)
            {
                var primeiro = nome?
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(primeiro))
                {
                    return primeiro;
                }
            }

            return string.Empty;
        }

        private static string? ObterTexto(IReadOnlyDictionary<string, object?>? dados, params string[] chaves)
        {
            if (dados == null)
            {
                return null;
            }

            foreach (var chave in chaves)
            {
                if (!dados.TryGetValue(chave, out var raw) || raw == null)
                {
                    continue;
                }

                switch (raw)
                {
                    case string texto when !string.IsNullOrWhiteSpace(texto):
                        return texto.Trim();
                    case JsonElement element when element.ValueKind == JsonValueKind.String:
                    {
                        var value = element.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return value.Trim();
                        }

                        break;
                    }
                }
            }

            return null;
        }

        private static void DecomporVeiculo(string? veiculoTexto, out string? marca, out string? modelo)
        {
            marca = null;
            modelo = null;

            if (string.IsNullOrWhiteSpace(veiculoTexto))
            {
                return;
            }

            var partes = veiculoTexto
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (partes.Length == 0)
            {
                return;
            }

            marca = partes[0];
            modelo = partes.Length == 1 ? null : string.Join(' ', partes.Skip(1));
        }

        private static ServicoCatalogVehicleItem? ResolverVeiculo(
            ServicoCatalogItem servico,
            string? marca,
            string? modelo,
            string? veiculoTexto)
        {
            var termo = MontarVeiculo(marca, modelo) ?? veiculoTexto;
            if (string.IsNullOrWhiteSpace(termo))
            {
                return null;
            }

            var marcaNormalizada = string.IsNullOrWhiteSpace(marca) ? null : marca.Trim().ToLowerInvariant();
            var modeloNormalizado = string.IsNullOrWhiteSpace(modelo) ? null : modelo.Trim().ToLowerInvariant();
            var termoNormalizado = termo.Trim().ToLowerInvariant();

            return servico.Veiculos
                .Where(item => item.Compativel)
                .FirstOrDefault(item =>
                {
                    var nome = item.NomeExibicao.ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(marcaNormalizada) && !nome.Contains(marcaNormalizada))
                    {
                        return false;
                    }

                    if (!string.IsNullOrWhiteSpace(modeloNormalizado))
                    {
                        return nome.Contains(modeloNormalizado);
                    }

                    return nome.Contains(termoNormalizado);
                });
        }

        private static ServicoCatalogPieceItem? ResolverMarcaPeca(ServicoCatalogVehicleItem veiculo, string? marcaPeca)
        {
            if (string.IsNullOrWhiteSpace(marcaPeca))
            {
                return null;
            }

            var normalized = marcaPeca.Trim();
            return veiculo.MarcasPeca.FirstOrDefault(item =>
                item.Nome.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                item.Id.Contains(normalized, StringComparison.OrdinalIgnoreCase));
        }
    }
}
