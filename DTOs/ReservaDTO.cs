using System;
using System.Collections.Generic;

namespace APIBack.DTOs
{
    public class MetricasDiaDTO
    {
        public int QuantidadeConfirmadas { get; set; }
        public int TotalPessoas { get; set; }
        public int TaxaOcupacao { get; set; }
    }

    public class ReservasDiaDTO
    {
        public DateTime DataReserva { get; set; }
        public IEnumerable<dynamic> Reservas { get; set; } = new List<dynamic>();
        public int TotalPessoasDia { get; set; }
    }
}
