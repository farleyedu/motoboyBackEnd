using System;
using System.Threading.Tasks;
using APIBack.DTOs.Cardapio;
using APIBack.DTOs.Common;

namespace APIBack.Service.Interface
{
    public interface ICardapioContractService
    {
        Task<bool> EstabelecimentoTemModuloAtivoAsync(Guid idEstabelecimento, string modulo);
        Task<PagedResultDto<CardapioCategoriaContractDto>> ListarCategoriasAsync(Guid idEstabelecimento, string? busca, bool? ativo, int page, int pageSize);
        Task<CardapioCategoriaContractDto?> ObterCategoriaPorIdAsync(Guid idEstabelecimento, Guid categoriaId);
        Task<CardapioCategoriaContractDto> CriarCategoriaAsync(Guid idEstabelecimento, SalvarCardapioCategoriaContractRequest request);
        Task<CardapioCategoriaContractDto> AtualizarCategoriaAsync(Guid idEstabelecimento, Guid categoriaId, SalvarCardapioCategoriaContractRequest request);
        Task<bool> AtualizarCategoriaStatusAsync(Guid idEstabelecimento, Guid categoriaId, bool ativo);
        Task<bool> ExcluirCategoriaAsync(Guid idEstabelecimento, Guid categoriaId);

        Task<PagedResultDto<CardapioAdicionalDto>> ListarAdicionaisAsync(Guid idEstabelecimento, string? busca, bool? ativo, int page, int pageSize);
        Task<CardapioAdicionalDto?> ObterAdicionalPorIdAsync(Guid idEstabelecimento, Guid adicionalId);
        Task<CardapioAdicionalDto> CriarAdicionalAsync(Guid idEstabelecimento, SalvarCardapioAdicionalRequest request);
        Task<CardapioAdicionalDto> AtualizarAdicionalAsync(Guid idEstabelecimento, Guid adicionalId, SalvarCardapioAdicionalRequest request);
        Task<bool> AtualizarAdicionalStatusAsync(Guid idEstabelecimento, Guid adicionalId, bool ativo);
        Task<bool> ExcluirAdicionalAsync(Guid idEstabelecimento, Guid adicionalId);

        Task<PagedResultDto<CardapioProdutoContractDto>> ListarProdutosAsync(Guid idEstabelecimento, string? busca, bool? ativo, Guid? categoriaId, int page, int pageSize);
        Task<CardapioProdutoContractDto?> ObterProdutoPorIdAsync(Guid idEstabelecimento, Guid produtoId);
        Task<CardapioProdutoContractDto> CriarProdutoAsync(Guid idEstabelecimento, SalvarCardapioProdutoContractRequest request);
        Task<CardapioProdutoContractDto> AtualizarProdutoAsync(Guid idEstabelecimento, Guid produtoId, SalvarCardapioProdutoContractRequest request);
        Task<bool> AtualizarProdutoStatusAsync(Guid idEstabelecimento, Guid produtoId, bool ativo);
        Task<bool> AtualizarProdutoDisponibilidadeAsync(Guid idEstabelecimento, Guid produtoId, bool disponivel);
        Task<bool> ExcluirProdutoAsync(Guid idEstabelecimento, Guid produtoId);

        Task<CardapioWebConfigDto> ObterWebConfigAsync(Guid idEstabelecimento);
        Task<CardapioWebConfigDto> SalvarWebConfigAsync(Guid idEstabelecimento, SalvarCardapioWebConfigRequest request);
        Task<CardapioWebConfigDto> AtualizarPublicacaoAsync(Guid idEstabelecimento, bool publicado);

        Task<CardapioPublicoSnapshotDto> ObterSnapshotPublicoAsync(Guid idEstabelecimento);
    }
}
