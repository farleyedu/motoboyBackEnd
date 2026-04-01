using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Model.Cardapio;

namespace APIBack.Repository.Interface
{
    public interface ICardapioRepository
    {
        Task<bool> EstabelecimentoTemModuloAtivoAsync(Guid idEstabelecimento, string modulo);
        Task<CardapioEstabelecimentoPublico?> ObterEstabelecimentoPublicoAsync(Guid? idEstabelecimento, string? estabelecimentoSlug);
        Task<CardapioWebConfig?> ObterWebConfigAsync(Guid idEstabelecimento);
        Task<CardapioWebConfig> UpsertWebConfigAsync(CardapioWebConfig entity);

        Task<(IReadOnlyCollection<CardapioCategoria> Itens, int Total)> ListarCategoriasAsync(
            Guid idEstabelecimento,
            string? busca,
            bool? ativo,
            int page,
            int pageSize);
        Task<bool> CategoriaTemProdutosAsync(Guid idEstabelecimento, Guid categoriaId);
        Task<CardapioCategoria?> ObterCategoriaPorIdAsync(Guid idEstabelecimento, Guid id);
        Task<Guid> CriarCategoriaAsync(CardapioCategoria entity);
        Task<bool> AtualizarCategoriaAsync(CardapioCategoria entity);
        Task<bool> AtualizarCategoriaStatusAsync(Guid idEstabelecimento, Guid id, bool ativo);
        Task<bool> ExcluirCategoriaAsync(Guid idEstabelecimento, Guid id);

        Task<(IReadOnlyCollection<CardapioGrupoAdicional> Itens, int Total)> ListarGruposAsync(
            Guid idEstabelecimento,
            string? busca,
            bool? ativo,
            int page,
            int pageSize,
            string? tipo = null);
        Task<IReadOnlyCollection<CardapioGrupoAdicional>> ListarGruposPorIdsAsync(Guid idEstabelecimento, IReadOnlyCollection<Guid> ids, string? tipo = null);
        Task<bool> GrupoTemProdutosAsync(Guid idEstabelecimento, Guid grupoId);
        Task<CardapioGrupoAdicional?> ObterGrupoPorIdAsync(Guid idEstabelecimento, Guid id, string? tipo = null);
        Task<Guid> CriarGrupoAsync(CardapioGrupoAdicional entity);
        Task<bool> AtualizarGrupoAsync(CardapioGrupoAdicional entity);
        Task<bool> AtualizarGrupoStatusAsync(Guid idEstabelecimento, Guid id, bool ativo);
        Task<bool> ExcluirGrupoAsync(Guid idEstabelecimento, Guid id);

        Task<(IReadOnlyCollection<CardapioProduto> Itens, int Total)> ListarProdutosAsync(
            Guid idEstabelecimento,
            string? busca,
            bool? ativo,
            bool? destaque,
            bool? disponivel,
            Guid? categoriaId,
            int page,
            int pageSize);
        Task<IReadOnlyCollection<CardapioProduto>> ListarProdutosPublicosAsync(Guid idEstabelecimento, string? busca);
        Task<IReadOnlyCollection<CardapioProduto>> ListarProdutosPublicosPorIdsAsync(Guid idEstabelecimento, IReadOnlyCollection<Guid> ids);
        Task<CardapioProduto?> ObterProdutoPorIdAsync(Guid idEstabelecimento, Guid id);
        Task<CardapioProduto?> ObterProdutoPublicoPorSlugAsync(Guid idEstabelecimento, string slug);
        Task<Guid> CriarProdutoAsync(CardapioProduto entity);
        Task<bool> AtualizarProdutoAsync(CardapioProduto entity);
        Task<bool> AtualizarProdutoStatusAsync(Guid idEstabelecimento, Guid id, bool ativo);
        Task<bool> AtualizarProdutoDisponibilidadeAsync(Guid idEstabelecimento, Guid id, bool disponivel);
        Task<bool> ExcluirProdutoAsync(Guid idEstabelecimento, Guid id);

        Task<Guid> CriarPedidoPublicoAsync(CardapioPedidoPublico entity);
    }
}
