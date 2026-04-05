using System;
using System.Threading.Tasks;
using APIBack.DTOs.Common;
using APIBack.DTOs.Configuracoes;

namespace APIBack.Service.Interface
{
    public interface IEstabelecimentoFaqService
    {
        Task<PagedResultDto<EstabelecimentoFaqDto>> ListarAsync(
            Guid idEstabelecimento,
            string? busca,
            bool? ativo,
            string? categoria,
            int page,
            int pageSize);
        Task<IReadOnlyCollection<EstabelecimentoFaqDto>> ListarTodosAsync(Guid idEstabelecimento);
        Task<EstabelecimentoFaqDto?> ObterPorIdAsync(Guid idEstabelecimento, Guid id);
        Task<EstabelecimentoFaqDto> CriarAsync(Guid idEstabelecimento, SalvarEstabelecimentoFaqRequest request);
        Task<EstabelecimentoFaqDto> AtualizarAsync(Guid idEstabelecimento, Guid id, SalvarEstabelecimentoFaqRequest request);
        Task<bool> AtualizarStatusAsync(Guid idEstabelecimento, Guid id, bool ativo);
        Task<bool> ExcluirAsync(Guid idEstabelecimento, Guid id);
    }
}
