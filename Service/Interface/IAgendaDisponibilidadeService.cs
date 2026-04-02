using System;
using System.Threading.Tasks;
using APIBack.DTOs.Agendamentos;
using APIBack.DTOs.Common;

namespace APIBack.Service.Interface
{
    public interface IAgendaDisponibilidadeService
    {
        Task<PagedResultDto<AgendaDisponibilidadeDto>> ListarAsync(
            Guid idEstabelecimento,
            bool? ativo,
            string? tipo,
            string? escopo,
            string? profissionalId,
            int page,
            int pageSize);
        Task<IReadOnlyCollection<AgendaDisponibilidadeDto>> ListarTodasAsync(Guid idEstabelecimento);

        Task<AgendaDisponibilidadeDto?> ObterPorIdAsync(Guid idEstabelecimento, Guid id);
        Task<AgendaDisponibilidadeDto> CriarAsync(Guid idEstabelecimento, SalvarAgendaDisponibilidadeRequest request);
        Task<AgendaDisponibilidadeDto> AtualizarAsync(Guid idEstabelecimento, Guid id, SalvarAgendaDisponibilidadeRequest request);
        Task<bool> AtualizarStatusAsync(Guid idEstabelecimento, Guid id, bool ativo);
        Task<bool> ExcluirAsync(Guid idEstabelecimento, Guid id);
        Task<PagedResultDto<ProfissionalDisponibilidadeDto>> ListarProfissionaisAsync(Guid idEstabelecimento);
    }
}
