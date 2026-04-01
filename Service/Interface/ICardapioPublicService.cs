using System;
using System.Threading.Tasks;
using APIBack.DTOs.Cardapio;

namespace APIBack.Service.Interface
{
    public interface ICardapioPublicService
    {
        Task<CardapioPublicoCatalogoDto> ObterCatalogoAsync(Guid? idEstabelecimento, string? estabelecimentoSlug, string? busca);
        Task<CardapioPublicoProdutoDto?> ObterProdutoAsync(Guid? idEstabelecimento, string? estabelecimentoSlug, string slug);
        Task<CardapioCotacaoDto> CalcularCotacaoAsync(CalcularCardapioPedidoPublicoRequest request);
        Task<CardapioPedidoPublicoCriadoDto> CriarPedidoAsync(CriarCardapioPedidoPublicoRequest request);
    }
}
