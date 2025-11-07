using System;

namespace APIBack.DTOs
{
    public class PeriodoMetricaDTO
    {
        public int TotalReservas { get; set; }
        public int TotalPessoas { get; set; }
        public DateTime? DataInicio { get; set; }
        public DateTime? DataFim { get; set; }
        public int? Numero { get; set; }
    }
}

