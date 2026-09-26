using System;
using System.Collections.Generic;
using System.Text.Json;
using APIBack.DTOs.Clientes;
using APIBack.DTOs.Delivery;

namespace APIBack.DTOs.Simulador
{
    // ---------------------------------------------------------------- eventos e sessoes

    public sealed class SimEventoDto
    {
        public long Id { get; set; }
        /// <summary>motoboy, cliente, pedido, conversa, sistema ou cenario.</summary>
        public string Entidade { get; set; } = "sistema";
        public string? EntidadeRef { get; set; }
        public string Tipo { get; set; } = string.Empty;
        public string Titulo { get; set; } = string.Empty;
        public string? Detalhe { get; set; }
        /// <summary>sucesso, atencao ou erro.</summary>
        public string Status { get; set; } = "sucesso";
        public string? CenarioId { get; set; }
        public string? UsuarioNome { get; set; }
        public DateTime CriadoEmUtc { get; set; }
    }

    public sealed class SimEventoRequest
    {
        public string? Entidade { get; set; }
        public string? EntidadeRef { get; set; }
        public string? Tipo { get; set; }
        public string? Titulo { get; set; }
        public string? Detalhe { get; set; }
        public string? Status { get; set; }
        public string? CenarioId { get; set; }
        public JsonElement? Dados { get; set; }
    }

    public sealed class SimMetricaDto
    {
        public int Total { get; set; }
        /// <summary>Variacao nas ultimas 24 h (pode ser negativa).</summary>
        public int Delta { get; set; }
        /// <summary>12 pontos de 2 h (do mais antigo ao mais novo) para o grafico pequeno.</summary>
        public int[] Serie { get; set; } = new int[12];
    }

    public sealed class SimSessaoResumoDto
    {
        public string Tipo { get; set; } = string.Empty;
        public int Ativas { get; set; }
        public DateTime? UltimaAtividadeUtc { get; set; }
    }

    public sealed class SimResumoDto
    {
        public SimMetricaDto Simulacoes { get; set; } = new();
        public SimMetricaDto Motoboys { get; set; } = new();
        public SimMetricaDto Clientes { get; set; } = new();
        public SimMetricaDto Pedidos { get; set; } = new();
        public SimMetricaDto Mensagens { get; set; } = new();
        public SimMetricaDto Alertas { get; set; } = new();
        public List<SimSessaoResumoDto> Sessoes { get; set; } = new();
    }

    public sealed class SimSessaoDto
    {
        public Guid Id { get; set; }
        public string Tipo { get; set; } = string.Empty;
        public string? Ref { get; set; }
        public string? Titulo { get; set; }
        public string Estado { get; set; } = "{}";
        public bool Ativa { get; set; }
        public DateTime CriadaEmUtc { get; set; }
        public DateTime UltimaAtividadeUtc { get; set; }
    }

    public sealed class SimSessaoRequest
    {
        public string? Tipo { get; set; }
        public string? Ref { get; set; }
        public string? Titulo { get; set; }
        public JsonElement? Estado { get; set; }
    }

    public sealed class SimIntegracaoDto
    {
        public string Chave { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;
        public string Descricao { get; set; } = string.Empty;
        /// <summary>ok, atencao ou indisponivel.</summary>
        public string Status { get; set; } = "ok";
        public string? Detalhe { get; set; }
    }

    // ---------------------------------------------------------------- cliente simulado

    public sealed class SimClienteDto
    {
        public ClienteDto Cliente { get; set; } = new();
        public int TotalPedidos { get; set; }
        public DateTime? UltimaAtividadeUtc { get; set; }
        /// <summary>Tag de exibicao: VIP, Inativo, Em tratamento, Frequente ou Novo.</summary>
        public string Etiqueta { get; set; } = "Novo";
        public Guid? ConversaId { get; set; }
    }

    public sealed class SimClienteListaDto
    {
        public List<SimClienteDto> Itens { get; set; } = new();
        public int Total { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
    }

    public sealed class SimConversaDto
    {
        public Guid? ConversaId { get; set; }
        public string? Estado { get; set; }
        /// <summary>true quando um atendente humano assumiu (a IA fica calada).</summary>
        public bool Humano { get; set; }
        public int NaoLidas { get; set; }
    }

    public sealed class SimPedidoResumoDto
    {
        public int Id { get; set; }
        public string IdExibicao { get; set; } = string.Empty;
        public string Status { get; set; } = "pendente";
        public string Origem { get; set; } = "atendente";
        public decimal? Total { get; set; }
        public DateTime? CriadoEm { get; set; }
        public string? NomeCliente { get; set; }
        public int? MotoboyId { get; set; }
        public string? MotoboyNome { get; set; }
    }

    // ---------------------------------------------------------------- pedido simulado

    public sealed class SimPedidoCriarRequest
    {
        public Guid? ClienteId { get; set; }
        public string? NomeCliente { get; set; }
        public string? TelefoneCliente { get; set; }
        /// <summary>whatsapp, app ou balcao.</summary>
        public string? Canal { get; set; }
        public string? Rua { get; set; }
        public string? Numero { get; set; }
        public string? Complemento { get; set; }
        public string? Bairro { get; set; }
        public string? Cidade { get; set; }
        public string? Estado { get; set; }
        public string? Cep { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public List<PedidoItemRequest>? Itens { get; set; }
        public string? Observacoes { get; set; }
        public string? TipoPagamento { get; set; }
        public decimal? Troco { get; set; }
        public decimal? TaxaEntrega { get; set; }
        public int? PrevisaoMinutos { get; set; }
        /// <summary>Liga o pedido a conversa do cliente e aceita os avisos de rastreio (so cliente de teste).</summary>
        public bool? AutomacoesReais { get; set; }
        /// <summary>Estado em que o pedido ja nasce ("criar pedido do nada"): recebido (padrao), confirmado, em_preparo, saiu, entregue ou cancelado.</summary>
        public string? StatusAlvo { get; set; }
        public int? MotoboyId { get; set; }
    }

    public sealed class SimPedidoStatusRequest
    {
        /// <summary>recebido, confirmado, em_preparo, saiu, entregue ou cancelado.</summary>
        public string? Alvo { get; set; }
        public int? MotoboyId { get; set; }
    }

    public sealed class SimPedidoEtapaRequest
    {
        /// <summary>confirmar, preparo, saiu ou entregue.</summary>
        public string? Etapa { get; set; }
        public int? MotoboyId { get; set; }
    }

    public sealed class SimPedidoEventoRequest
    {
        /// <summary>pagamento_confirmado, atraso, cliente_ligou, falha_gps ou nota.</summary>
        public string? Tipo { get; set; }
        public int? Minutos { get; set; }
        public string? Texto { get; set; }
    }

    public sealed class SimPedidoMotoboyRequest
    {
        public int MotoboyId { get; set; }
    }

    public sealed class SimPedidoAnexarRequest
    {
        public Guid ClienteId { get; set; }
    }

    public sealed class SimMotoboyCardDto
    {
        public int Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Telefone { get; set; }
        public string? Avatar { get; set; }
        public int Entregas { get; set; }
        public bool Online { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
    }

    public sealed class SimPedidoDetalheDto
    {
        public PedidoDetalheDto Pedido { get; set; } = new();
        public string IdExibicao { get; set; } = string.Empty;
        public bool Simulado { get; set; }
        public string? Canal { get; set; }
        public Guid? ClienteId { get; set; }
        /// <summary>recebido, confirmado, em_preparo, saiu, entregue ou cancelado.</summary>
        public string Etapa { get; set; } = "recebido";
        public DateTime? ConfirmadoEm { get; set; }
        public DateTime? PreparoEm { get; set; }
        public DateTime? SaiuEm { get; set; }
        public DateTime? EntregueEm { get; set; }
        public SimMotoboyCardDto? Motoboy { get; set; }
        public double? RestauranteLatitude { get; set; }
        public double? RestauranteLongitude { get; set; }
        public string? RestauranteNome { get; set; }
        public double? DistanciaKm { get; set; }
        public int? EtaMinutos { get; set; }
        /// <summary>sem_motoboy, aguardando, em_rota, concluida ou cancelada.</summary>
        public string RotaStatus { get; set; } = "sem_motoboy";
        /// <summary>Proximos passos validos (para habilitar os botoes): confirmar, preparo, saiu, entregue.</summary>
        public List<string> Proximos { get; set; } = new();
    }
}
