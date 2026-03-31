using System;
using System.Collections.Generic;

namespace APIBack.DTOs.Agendamentos
{
    public class AgendaDisponibilidadeDto
    {
        public Guid Id { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string Escopo { get; set; } = "estabelecimento";
        public string? ProfissionalId { get; set; }
        public string Tipo { get; set; } = "disponibilidade_semanal";
        public List<int> DiasSemana { get; set; } = new();
        public DateOnly? DataInicio { get; set; }
        public DateOnly? DataFim { get; set; }
        public TimeOnly? HoraInicio { get; set; }
        public TimeOnly? HoraFim { get; set; }
        public bool DiaInteiro { get; set; }
        public bool Ativo { get; set; }
        public string? Observacao { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
    }

    public class SalvarAgendaDisponibilidadeRequest
    {
        public string Escopo { get; set; } = "estabelecimento";
        public string? ProfissionalId { get; set; }
        public string Tipo { get; set; } = "disponibilidade_semanal";
        public List<int>? DiasSemana { get; set; }
        public DateOnly? DataInicio { get; set; }
        public DateOnly? DataFim { get; set; }
        public TimeOnly? HoraInicio { get; set; }
        public TimeOnly? HoraFim { get; set; }
        public bool DiaInteiro { get; set; }
        public bool Ativo { get; set; } = true;
        public string? Observacao { get; set; }
    }

    public class ProfissionalDisponibilidadeDto
    {
        public string Id { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;
        public bool Ativo { get; set; }
    }
}
