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
        /// <summary>Acha o endereco no mapa para o cliente ver o pino antes de enviar o pedido.</summary>
        Task<CardapioLocalizacaoDto> LocalizarEnderecoAsync(LocalizarCardapioEnderecoRequest request);
        /// <summary>Endereco de um ponto do mapa ("usar minha localizacao").</summary>
        Task<CardapioEnderecoDoPontoDto> ObterEnderecoDoPontoAsync(CardapioEnderecoDoPontoRequest request);

        /// <summary>Prefill seguro do checkout pelo telefone (Fase 3c): ver IdentificarClienteRecenteAsync.</summary>
        Task<CardapioClienteIdentificadoDto> IdentificarClienteAsync(Guid? idEstabelecimento, string? estabelecimentoSlug, string? telefone);
    }
}
