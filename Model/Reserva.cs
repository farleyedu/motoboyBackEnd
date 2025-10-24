using System;

namespace APIBack.Model
{
    public class Reserva
    {
        public long Id { get; set; }
        public Guid IdCliente { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public long? IdProfissional { get; set; }
        public long? IdServico { get; set; }

        /// <summary>
        /// Nome completo informado pelo cliente no momento da reserva
        /// </summary>
        public string? NomeCliente { get; set; }

        public int? QtdPessoas { get; set; }
        public DateTime DataReserva { get; set; }
        public TimeSpan HoraInicio { get; set; }
        public TimeSpan? HoraFim { get; set; }
        public ReservaStatus Status { get; set; } = ReservaStatus.Confirmado;
        public string? Observacoes { get; set; }
        public DateTime DataCriacao { get; set; }
        public DateTime DataAtualizacao { get; set; }
    }
}

