using APIBack.Model.Enum;
using System.Text.Json.Serialization;

namespace APIBack.DTOs
{
    public class PedidoDTOs
    {
        public int Id { get; set; }
        public string NomeCliente { get; set; } = "";
        public string EnderecoEntrega { get; set; } = "";
        public string? IdIfood { get; set; }
        public string? TelefoneCliente { get; set; }
        public DateTime DataPedido { get; set; }
        public StatusPedido StatusPedido { get; set; } = StatusPedido.Pendente;
        public string? HorarioPedido { get; set; }
        public string? PrevisaoEntrega { get; set; }
        public string? HorarioSaida { get; set; }
        public string? HorarioEntrega { get; set; }
        public string? Items { get; set; }
        public decimal? Value { get; set; }
        public string? Region { get; set; }

        public string? Latitude { get; set; }
        public string? Longitude { get; set; }

        public string[] Coordinates => new[] { Longitude, Latitude };

        public MotoboyDTO? MotoboyResponsavel { get; set; }
    }

    public class MotoboyDTO
    {
        public int Id { get; set; }
        public string Nome { get; set; } = "";
        public string? Avatar { get; set; }
        public string Status { get; set; } = "offline";
    }

    //recebe dados do ZippyRobot

    public class PedidoCapturado
    {
        public string? Id { get; set; }

        [JsonPropertyName("pedidoIdIfood")]
        public string PedidoIdIfood { get; set; }

        public string DisplayId { get; set; }
        public string? Localizador { get; set; }

        public DateTime? CriadoEm { get; set; }
        public DateTime? PrevisaoEntrega { get; set; }
        public DateTime? HorarioEntrega { get; set; }
        public DateTime? HorarioSaida { get; set; }

        public Coordenadas? Coordenadas { get; set; }
        public Cliente Cliente { get; set; }
        public Endereco Endereco { get; set; }

        public List<ItemPedido> Itens { get; set; }

        [JsonPropertyName("tipoPagamento")]
        public string? TipoPagamento { get; set; }  // <== ADICIONADO
    }

    public class Coordenadas
    {
        public string? Latitude { get; set; }
        public string? Longitude { get; set; }
    }

    public class Cliente
    {
        public string? Nome { get; set; }
        public string? Telefone { get; set; }
        public string? Documento { get; set; }
    }

    public class Endereco
    {
        public string? Rua { get; set; }
        public string? Numero { get; set; }
        public string? Bairro { get; set; }
        public string? Cidade { get; set; }
        public string? Estado { get; set; }
        public string? Cep { get; set; }
        public string? Complemento { get; set; }
    }

    public class ItemPedido
    {
        public string? Nome { get; set; }
        public int? Quantidade { get; set; }

        [JsonPropertyName("preco")]
        public decimal? PrecoUnitario { get; set; }

        public decimal? PrecoTotal { get; set; }
    }



}
