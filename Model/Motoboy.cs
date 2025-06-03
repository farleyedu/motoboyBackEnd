namespace APIBack.Model
{
    public class Motoboy
    {
        public int? Id { get; set; }
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
        public double Latitude { get; set; }
        public double Longitude { get; set; }

    }
}
