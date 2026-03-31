using System;
using System.Collections.Generic;

namespace APIBack.Model
{
    public class AgendaDisponibilidade
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public long? ProfissionalId { get; set; }
        public string Escopo { get; set; } = "estabelecimento";
        public string Tipo { get; set; } = "disponibilidade_semanal";
        public List<int> DiasSemana { get; set; } = new();
        public DateOnly? DataInicio { get; set; }
        public DateOnly? DataFim { get; set; }
        public TimeOnly? HoraInicio { get; set; }
        public TimeOnly? HoraFim { get; set; }
        public bool DiaInteiro { get; set; }
        public bool Ativo { get; set; } = true;
        public string? Observacao { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
    }
}
