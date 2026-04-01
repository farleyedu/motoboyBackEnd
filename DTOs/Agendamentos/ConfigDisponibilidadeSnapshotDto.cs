using System.Collections.Generic;

namespace APIBack.DTOs.Agendamentos
{
    public class ConfigDisponibilidadeSnapshotDto
    {
        public EstabelecimentoAgendamentoConfigDto Configuracao { get; set; } = new();
        public List<AgendaDisponibilidadeDto> Regras { get; set; } = new();
        public List<ProfissionalDisponibilidadeDto> Profissionais { get; set; } = new();
    }
}
