using System;

namespace APIBack.Model
{
    public class EstabelecimentoAgendamentoConfig
    {
        public Guid IdEstabelecimento { get; set; }
        public bool AgendaAtiva { get; set; }
        public bool ExigeServico { get; set; }
        public bool ExigeProfissional { get; set; }
        public bool PermiteEncaixe { get; set; }
        public bool AgendaInformativo { get; set; }
        public int DuracaoSlotMinutos { get; set; } = 30;
        public int IntervaloEntreSlotsMinutos { get; set; }
        public int LimitePorSlot { get; set; } = 1;
        public int AntecedenciaMinimaHoras { get; set; }
        public int AntecedenciaMaximaDias { get; set; } = 30;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
