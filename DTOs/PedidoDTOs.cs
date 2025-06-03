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
        public string StatusPedido { get; set; } = "";
        public string? HorarioPedido { get; set; }
        public string? PrevisaoEntrega { get; set; }
        public string? HorarioSaida { get; set; }
        public string? HorarioEntrega { get; set; }
        public string? Items { get; set; }
        public decimal? Value { get; set; }
        public string? Region { get; set; }

        public double Latitude { get; set; }
        public double Longitude { get; set; }

        public double[] Coordinates => new[] { Longitude, Latitude };

        public MotoboyDTO? MotoboyResponsavel { get; set; }
    }

    public class MotoboyDTO
    {
        public int Id { get; set; }
        public string Nome { get; set; } = "";
        public string? Avatar { get; set; }
        public string Status { get; set; } = "offline";
    }

}
