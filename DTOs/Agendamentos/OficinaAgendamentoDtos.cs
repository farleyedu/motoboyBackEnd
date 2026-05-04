using System;
using System.Collections.Generic;

namespace APIBack.DTOs.Agendamentos
{
    public class OficinaAgendamentoDto
    {
        public Guid Id { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public Guid ClienteId { get; set; }
        public Guid ConversaId { get; set; }
        public Guid? AtendimentoServicoId { get; set; }
        public Guid? ServicoId { get; set; }
        public long? ProfissionalId { get; set; }
        public string? NomeCliente { get; set; }
        public string TelefoneE164 { get; set; } = string.Empty;
        public string NomeServico { get; set; } = string.Empty;
        public string? VeiculoMarca { get; set; }
        public string? VeiculoModelo { get; set; }
        public string? MarcaPeca { get; set; }
        public DateTime DataAgendamento { get; set; }
        public string HoraInicio { get; set; } = string.Empty;
        public string HoraFim { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Codigo { get; set; } = string.Empty;
        public string? Observacao { get; set; }
        public Dictionary<string, object?> DadosExtras { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTime DataCriacao { get; set; }
        public DateTime DataAtualizacao { get; set; }
        public DateTime? DataCancelamento { get; set; }
    }

    public class OficinaSlotDto
    {
        public DateTime Data { get; set; }
        public string HoraInicio { get; set; } = string.Empty;
        public string HoraFim { get; set; } = string.Empty;
        public long? ProfissionalId { get; set; }
    }

    public class CriarOficinaAgendamentoRequest
    {
        public Guid ConversaId { get; set; }
        public Guid? AtendimentoServicoId { get; set; }
        public Guid? ServicoId { get; set; }
        public long? ProfissionalId { get; set; }
        public string? NomeCliente { get; set; }
        public string NomeServico { get; set; } = string.Empty;
        public string? VeiculoMarca { get; set; }
        public string? VeiculoModelo { get; set; }
        public string? MarcaPeca { get; set; }
        public DateTime DataAgendamento { get; set; }
        public string HoraInicio { get; set; } = string.Empty;
        public int? DuracaoMinutos { get; set; }
        public string? Observacao { get; set; }
        public Dictionary<string, object?>? DadosExtras { get; set; }
    }

    public class RemarcarOficinaAgendamentoRequest
    {
        public DateTime DataAgendamento { get; set; }
        public string HoraInicio { get; set; } = string.Empty;
        public int? DuracaoMinutos { get; set; }
        public long? ProfissionalId { get; set; }
    }
}
