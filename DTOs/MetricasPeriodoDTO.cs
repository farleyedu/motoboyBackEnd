using System;

namespace APIBack.DTOs
{
    public class MetricasPeriodoDTO
    {
        public int QuantidadeReservas { get; set; }
        public int TotalPessoas { get; set; }
        public int TaxaOcupacao { get; set; }
        public PeriodoDTO Periodo { get; set; } = new();
    }

    public class PeriodoDTO
    {
        public DateTime? Inicio { get; set; }
        public DateTime? Fim { get; set; }
    }
}

