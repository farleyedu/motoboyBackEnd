using APIBack.Model.Enum;

namespace APIBack.DTOs
{
    public class MotoboyDetalhadoDTO
    {
        public int Id { get; set; }
        public string? Nome { get; set; }
        public string? Avatar { get; set; }
        public string? Cnh { get; set; }
        public string? Telefone { get; set; }
        public string? PlacaMoto { get; set; }
        public string? MarcaMoto { get; set; }
        public string? ModeloMoto { get; set; }
        public string? RenavamMoto { get; set; }
        public int Status { get; set; }
        public int QtdPedidosAtivos { get; set; }
    }

    //public class MotoboyMapaDTO
    //{
    //    public int Id { get; set; }
    //    public string? Nome { get; set; }
    //    public StatusMotoboy Status { get; set; } 
    //    public double Latitude { get; set; }
    //    public double Longitude { get; set; }
    //}

    public class MotoboyComPedidosDTO
    {
        public int Id { get; set; }
        public string Nome { get; set; } = "";
        public string? Avatar { get; set; }
        public string Status { get; set; } = "offline";
        public string? Telefone { get; set; }

        public double[] Location => new[] { Longitude, Latitude }; // 👈 aqui o array já montado
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public List<PedidoDTO> pedidos { get; set; } = new();
    }


    public class PedidoDTO
    {
        public int Id { get; set; }
        public string Status { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string Items { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string DepartureTime { get; set; } = string.Empty;
        public string Eta { get; set; } = string.Empty;
        public int EtaMinutes { get; set; }
        public double[] Coordinates { get; set; } = new double[2];
    }
}
