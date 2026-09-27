namespace APIBack.DTOs.Delivery
{
    /// <summary>
    /// Metricas reais dos ultimos 7 dias (nunca estimadas): so o que da para calcular a partir do
    /// historico de entregas do proprio estabelecimento. Sem amostra suficiente, os campos ficam null
    /// e AmostraSuficiente = false - a tela deve mostrar "sem dados suficientes ainda", nunca um numero
    /// inventado.
    /// </summary>
    public sealed class ImpactoOperacionalDto
    {
        public int EntregasConcluidas7Dias { get; set; }
        public bool AmostraSuficiente { get; set; }
        public double? TempoMedioMinutos { get; set; }
        public double? TempoMedioMinutosSemanaAnterior { get; set; }
        public decimal? CustoMedioEntrega { get; set; }
    }
}
