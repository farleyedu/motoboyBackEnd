using System;
using System.Threading.Tasks;
using APIBack.DTOs.Cardapio;
using APIBack.DTOs.Common;

namespace APIBack.Service.Interface
{
    public interface ICardapioService
    {
        Task<bool> EstabelecimentoTemModuloAtivoAsync(Guid idEstabelecimento, string modulo);
        Task<CardapioSnapshotDto> ObterSnapshotAsync(Guid idEstabelecimento);

        Task<PagedResultDto<CardapioCategoriaDto>> ListarCategoriasAsync(
            Guid idEstabelecimento,
            string? busca,
            bool? ativo,
            int page,
            int pageSize);
        Task<CardapioCategoriaDto?> ObterCategoriaPorIdAsync(Guid idEstabelecimento, Guid id);
        Task<CardapioCategoriaDto> CriarCategoriaAsync(Guid idEstabelecimento, SalvarCardapioCategoriaRequest request);
        Task<CardapioCategoriaDto> AtualizarCategoriaAsync(Guid idEstabelecimento, Guid id, SalvarCardapioCategoriaRequest request);
        Task<bool> AtualizarCategoriaStatusAsync(Guid idEstabelecimento, Guid id, bool ativo);
        Task<bool> ExcluirCategoriaAsync(Guid idEstabelecimento, Guid id);

        Task<PagedResultDto<CardapioGrupoAdicionalDto>> ListarGruposAsync(
            Guid idEstabelecimento,
            string? busca,
            bool? ativo,
            int page,
            int pageSize);
        Task<CardapioGrupoAdicionalDto?> ObterGrupoPorIdAsync(Guid idEstabelecimento, Guid id);
        Task<CardapioGrupoAdicionalDto> CriarGrupoAsync(Guid idEstabelecimento, SalvarCardapioGrupoAdicionalRequest request);
        Task<CardapioGrupoAdicionalDto> AtualizarGrupoAsync(Guid idEstabelecimento, Guid id, SalvarCardapioGrupoAdicionalRequest request);
        Task<bool> AtualizarGrupoStatusAsync(Guid idEstabelecimento, Guid id, bool ativo);
        Task<bool> ExcluirGrupoAsync(Guid idEstabelecimento, Guid id);

        Task<PagedResultDto<CardapioProdutoDto>> ListarProdutosAsync(
            Guid idEstabelecimento,
            string? busca,
            bool? ativo,
            bool? destaque,
            bool? disponivel,
            Guid? categoriaId,
            int page,
            int pageSize);
        Task<CardapioProdutoDto?> ObterProdutoPorIdAsync(Guid idEstabelecimento, Guid id);
        Task<CardapioProdutoDto> CriarProdutoAsync(Guid idEstabelecimento, SalvarCardapioProdutoRequest request);
        Task<CardapioProdutoDto> AtualizarProdutoAsync(Guid idEstabelecimento, Guid id, SalvarCardapioProdutoRequest request);
        Task<bool> AtualizarProdutoStatusAsync(Guid idEstabelecimento, Guid id, bool ativo);
        Task<bool> AtualizarProdutoDisponibilidadeAsync(Guid idEstabelecimento, Guid id, bool disponivel);
        Task<bool> ExcluirProdutoAsync(Guid idEstabelecimento, Guid id);
    }
}
