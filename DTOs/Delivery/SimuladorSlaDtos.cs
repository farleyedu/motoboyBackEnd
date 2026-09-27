namespace APIBack.DTOs.Delivery
{
    public sealed class SimularSlaRequest
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

    /// <summary>Resultado do simulador de SLA: mesmo calculo de taxa/zona do nucleo de pedido, aplicado
    /// a um endereco hipotetico. Previsao de tempo e uma estimativa (preparo + deslocamento), nunca uma
    /// medida real.</summary>
    public sealed class SimuladorSlaResultDto
    {
        public string? ZonaNome { get; set; }
        public double? DistanciaKm { get; set; }
        public bool DentroDoRaio { get; set; } = true;
        public decimal TaxaEntrega { get; set; }
        public int PrevisaoMinMinutos { get; set; }
        public int PrevisaoMaxMinutos { get; set; }
        public bool EstabelecimentoAbertoAgora { get; set; }
    }
}
