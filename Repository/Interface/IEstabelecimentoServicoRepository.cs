using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Model;

namespace APIBack.Repository.Interface
{
    public interface IEstabelecimentoServicoRepository
    {
        Task<(IReadOnlyCollection<EstabelecimentoServico> Itens, int Total)> ListarAsync(
            Guid idEstabelecimento,
            string? busca,
            bool? ativo,
            bool? agendavel,
            string? tipo,
            int page,
            int pageSize);
        Task<IReadOnlyCollection<EstabelecimentoServico>> ListarTodosAsync(Guid idEstabelecimento);
        Task<EstabelecimentoServico?> ObterPorIdAsync(Guid idEstabelecimento, Guid id);
        Task<Guid> CriarAsync(EstabelecimentoServico entity);
        Task<bool> AtualizarAsync(EstabelecimentoServico entity);
        Task<bool> AtualizarStatusAsync(Guid idEstabelecimento, Guid id, bool ativo);
        Task<bool> ExcluirAsync(Guid idEstabelecimento, Guid id);
    }
}
