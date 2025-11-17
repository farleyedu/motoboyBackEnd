using System;
using System.Collections.Generic;

namespace APIBack.DTOs
{
    public class ReservasDiaDTO
    {
        public DateTime DataReserva { get; set; }
        public IEnumerable<dynamic> Reservas { get; set; } = new List<dynamic>();
        public int TotalPessoasDia { get; set; }
    }
}

