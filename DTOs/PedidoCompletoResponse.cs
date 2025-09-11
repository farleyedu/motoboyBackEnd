using System.Text.Json.Serialization;

namespace APIBack.DTOs
{
    /// <summary>
    /// DTO completo para retorno detalhado de pedidos com todas as informações necessárias
    /// </summary>
    public class PedidoCompletoResponse
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("idIfood")]
        public string? IdIfood { get; set; }

        // === DADOS DO CLIENTE ===
        [JsonPropertyName("nomeCliente")]
        public string NomeCliente { get; set; } = string.Empty;

        [JsonPropertyName("telefoneCliente")]
        public string TelefoneCliente { get; set; } = string.Empty;

        // === ENDEREÇO COMPLETO ===
        [JsonPropertyName("enderecoEntrega")]
        public string EnderecoEntrega { get; set; } = string.Empty;

        [JsonPropertyName("bairro")]
        public string Bairro { get; set; } = string.Empty;

        // === COORDENADAS ===
        [JsonPropertyName("coordinates")]
        public CoordinatesDto Coordinates { get; set; } = new();

        // === VALORES E PAGAMENTO ===
        [JsonPropertyName("valorTotal")]
        public decimal ValorTotal { get; set; }

        [JsonPropertyName("tipoPagamento")]
        public string TipoPagamento { get; set; } = string.Empty;

        [JsonPropertyName("statusPagamento")]
        public string StatusPagamento { get; set; } = "a_receber";

        [JsonPropertyName("troco")]
        public decimal? Troco { get; set; }

        // === ITENS DO PEDIDO ===
        [JsonPropertyName("itens")]
        public List<ItemPedidoDto> Itens { get; set; } = new();

        // === HORÁRIOS ===
        [JsonPropertyName("horarioPedido")]
        public string HorarioPedido { get; set; } = string.Empty;

        [JsonPropertyName("horarioFormatado")]
        public string HorarioFormatado { get; set; } = string.Empty;

        [JsonPropertyName("dataPedido")]
        public string DataPedido { get; set; } = string.Empty;

        // === STATUS E CONTROLE ===
        [JsonPropertyName("statusPedido")]
        public string StatusPedido { get; set; } = "disponivel";

        [JsonPropertyName("motoboyResponsavel")]
        public string? MotoboyResponsavel { get; set; }

        // === DISTÂNCIA ===
        [JsonPropertyName("distanciaKm")]
        public double DistanciaKm { get; set; }

        // === TIMELINE ===
        [JsonPropertyName("timeline")]
        public List<TimelineDto> Timeline { get; set; } = new();

        // === CÓDIGOS E OBSERVAÇÕES ===
        [JsonPropertyName("codigoEntrega")]
        public string? CodigoEntrega { get; set; }

        [JsonPropertyName("observacoes")]
        public string? Observacoes { get; set; }
    }

    /// <summary>
    /// DTO para coordenadas geográficas
    /// </summary>
    public class CoordinatesDto
    {
        [JsonPropertyName("lat")]
        public double Lat { get; set; }

        [JsonPropertyName("lng")]
        public double Lng { get; set; }
    }

    /// <summary>
    /// DTO para itens do pedido
    /// </summary>
    public class ItemPedidoDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("nome")]
        public string Nome { get; set; } = string.Empty;

        [JsonPropertyName("quantidade")]
        public int Quantidade { get; set; }

        [JsonPropertyName("valor")]
        public decimal Valor { get; set; }

        [JsonPropertyName("tipo")]
        public string Tipo { get; set; } = "comida";

        [JsonPropertyName("observacoes")]
        public string? Observacoes { get; set; }
    }

    /// <summary>
    /// DTO para timeline de eventos do pedido
    /// </summary>
    public class TimelineDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("evento")]
        public string Evento { get; set; } = string.Empty;

        [JsonPropertyName("horario")]
        public string Horario { get; set; } = string.Empty;

        [JsonPropertyName("local")]
        public string Local { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = "pendente";
    }
}