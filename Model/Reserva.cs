using System;

namespace APIBack.Model
{
    public class Reserva
    {
        public long Id { get; set; }
        public long IdCliente { get; set; }
        public long IdEstabelecimento { get; set; }
        public long? IdProfissional { get; set; }
        public long? IdServico { get; set; }
        public int? QtdPessoas { get; set; }
        public DateTime DataReserva { get; set; }
        public TimeSpan HoraInicio { get; set; }
        public TimeSpan? HoraFim { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? Observacoes { get; set; }
        public DateTime DataCriacao { get; set; }
        public DateTime DataAtualizacao { get; set; }
    }
}
