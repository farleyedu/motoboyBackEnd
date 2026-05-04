using System;
using System.Collections.Generic;

namespace APIBack.Model
{
    public class OficinaAgendamento
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public Guid IdCliente { get; set; }
        public Guid IdConversa { get; set; }
        public Guid? IdAtendimentoServico { get; set; }
        public Guid? IdServico { get; set; }
        public long? IdProfissional { get; set; }
        public string? NomeCliente { get; set; }
        public string TelefoneE164 { get; set; } = string.Empty;
        public string NomeServico { get; set; } = string.Empty;
        public string? VeiculoMarca { get; set; }
        public string? VeiculoModelo { get; set; }
        public string? MarcaPeca { get; set; }
        public DateTime DataAgendamento { get; set; }
        public TimeSpan HoraInicio { get; set; }
        public TimeSpan HoraFim { get; set; }
        public string Status { get; set; } = "confirmado";
        public string Codigo { get; set; } = string.Empty;
        public string? Observacao { get; set; }
        public Dictionary<string, object?> DadosExtras { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTime DataCriacao { get; set; }
        public DateTime DataAtualizacao { get; set; }
        public DateTime? DataCancelamento { get; set; }
    }
}
