using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Model;

namespace APIBack.Repository.Interface
{
    public interface IAgendaDisponibilidadeRepository
    {
        Task<(IReadOnlyCollection<AgendaDisponibilidade> Itens, int Total)> ListarAsync(
            Guid idEstabelecimento,
            bool? ativo,
            string? tipo,
            string? escopo,
            long? profissionalId,
            int page,
            int pageSize);

        Task<AgendaDisponibilidade?> ObterPorIdAsync(Guid idEstabelecimento, Guid id);
        Task<Guid> CriarAsync(AgendaDisponibilidade entity);
        Task<bool> AtualizarAsync(AgendaDisponibilidade entity);
        Task<bool> AtualizarStatusAsync(Guid idEstabelecimento, Guid id, bool ativo);
        Task<bool> ExcluirAsync(Guid idEstabelecimento, Guid id);
        Task<bool> ProfissionalExisteNoEstabelecimentoAsync(Guid idEstabelecimento, long profissionalId);
        Task<IReadOnlyCollection<(long Id, string Nome, bool Ativo)>> ListarProfissionaisAsync(Guid idEstabelecimento);
        Task<bool> ExisteConflitoAsync(Guid idEstabelecimento, AgendaDisponibilidade entity);
    }
}
