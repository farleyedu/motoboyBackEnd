using System;

namespace APIBack.DTOs.Agendamentos
{
    public class EstabelecimentoAgendamentoConfigDto
    {
        public Guid EstabelecimentoId { get; set; }
        public bool AgendaAtiva { get; set; }
        public bool ExigeServico { get; set; }
        public bool ExigeProfissional { get; set; }
        public bool PermiteEncaixe { get; set; }
        public bool AgendaInformativo { get; set; }
        public int DuracaoSlotMinutos { get; set; }
        public int IntervaloEntreSlotsMinutos { get; set; }
        public int LimitePorSlot { get; set; }
        public int AntecedenciaMinimaHoras { get; set; }
        public int AntecedenciaMaximaDias { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class SalvarEstabelecimentoAgendamentoConfigRequest
    {
        public bool AgendaAtiva { get; set; }
        public bool ExigeServico { get; set; }
        public bool ExigeProfissional { get; set; }
        public bool PermiteEncaixe { get; set; }
        public bool AgendaInformativo { get; set; }
        public int DuracaoSlotMinutos { get; set; }
        public int IntervaloEntreSlotsMinutos { get; set; }
        public int LimitePorSlot { get; set; }
        public int AntecedenciaMinimaHoras { get; set; }
        public int AntecedenciaMaximaDias { get; set; }
    }
}
