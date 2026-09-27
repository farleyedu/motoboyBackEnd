using System;

namespace APIBack.DTOs.Delivery
{
    /// <summary>Faixa de raio com taxa propria (Zona 1, Zona 2...). A zona de um pedido e a de menor
    /// RaioAteKm que ainda cobre a distancia calculada (linha reta, mesma formula do nucleo de pedido).</summary>
    public sealed class DeliveryZonaDto
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public decimal RaioAteKm { get; set; }
        public decimal Taxa { get; set; }
        public string? Cor { get; set; }
        public int Ordem { get; set; }
        public bool Ativo { get; set; } = true;
    }

    public sealed class SalvarZonaRequest
    {
        public string Nome { get; set; } = string.Empty;
        public decimal RaioAteKm { get; set; }
        public decimal Taxa { get; set; }
        public string? Cor { get; set; }
        public int Ordem { get; set; }
        public bool Ativo { get; set; } = true;
    }
}
