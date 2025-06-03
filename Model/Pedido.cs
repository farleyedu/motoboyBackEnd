using APIBack.Model.Enum;

namespace APIBack.Model
{
    public class Pedido
    {
        public int? Id { get; set; }
        public string? NomeCliente { get; set; }
        public string? EnderecoEntrega { get; set; }
        public string? IdIfood { get; set; }
        public string? TelefoneCliente { get; set; }
        public DateTime DataPedido { get; set; }
        public StatusPedido? StatusPedido { get; set; }
        public int? MotoboyResponsavel { get; set; }
    }
}
