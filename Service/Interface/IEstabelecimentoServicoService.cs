using System;
using System.Threading.Tasks;
using APIBack.DTOs.Common;
using APIBack.DTOs.Configuracoes;

namespace APIBack.Service.Interface
{
    public interface IEstabelecimentoServicoService
    {
        Task<PagedResultDto<EstabelecimentoServicoDto>> ListarAsync(
            Guid idEstabelecimento,
            string? busca,
            bool? ativo,
            bool? agendavel,
            string? tipo,
            int page,
            int pageSize);
        Task<EstabelecimentoServicoDto?> ObterPorIdAsync(Guid idEstabelecimento, Guid id);
        Task<EstabelecimentoServicoDto> CriarAsync(Guid idEstabelecimento, SalvarEstabelecimentoServicoRequest request);
        Task<EstabelecimentoServicoDto> AtualizarAsync(Guid idEstabelecimento, Guid id, SalvarEstabelecimentoServicoRequest request);
        Task<bool> AtualizarStatusAsync(Guid idEstabelecimento, Guid id, bool ativo);
        Task<bool> ExcluirAsync(Guid idEstabelecimento, Guid id);
    }
}
