using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Model;

namespace APIBack.Repository.Interface
{
    public interface IEstabelecimentoFaqRepository
    {
        Task<(IReadOnlyCollection<EstabelecimentoFaq> Itens, int Total)> ListarAsync(
            Guid idEstabelecimento,
            string? busca,
            bool? ativo,
            string? categoria,
            int page,
            int pageSize);
        Task<IReadOnlyCollection<EstabelecimentoFaq>> ListarTodosAsync(Guid idEstabelecimento);
        Task<EstabelecimentoFaq?> ObterPorIdAsync(Guid idEstabelecimento, Guid id);
        Task<Guid> CriarAsync(EstabelecimentoFaq entity);
        Task<bool> AtualizarAsync(EstabelecimentoFaq entity);
        Task<bool> AtualizarStatusAsync(Guid idEstabelecimento, Guid id, bool ativo);
        Task<bool> ExcluirAsync(Guid idEstabelecimento, Guid id);
    }
}
