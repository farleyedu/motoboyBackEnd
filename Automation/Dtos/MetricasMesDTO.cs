namespace APIBack.DTOs
{
    public class MetricasMesDTO
    {
        public bool IsMesAtual { get; set; }
        public PeriodoMetricaDTO? Hoje { get; set; }
        public PeriodoMetricaDTO? SemanaVigente { get; set; }
        public PeriodoMetricaDTO? QuinzenaVigente { get; set; }
        public PeriodoMetricaDTO? MesVigente { get; set; }
        public PeriodoMetricaDTO? MesSelecionado { get; set; }
    }
}

