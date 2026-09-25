using System;
using System.Threading.Tasks;
using APIBack.DTOs.Cardapio;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;

namespace APIBack.Service
{
    public sealed class CardapioFichaService : ICardapioFichaService
    {
        private readonly ICardapioRepository _cardapio;
        private readonly IProdutoAtendimentoRepository _atendimento;
        private readonly IRestaurantSettingsRepository _restaurant;

        public CardapioFichaService(
            ICardapioRepository cardapio,
            IProdutoAtendimentoRepository atendimento,
            IRestaurantSettingsRepository restaurant)
        {
            _cardapio = cardapio;
            _atendimento = atendimento;
            _restaurant = restaurant;
        }

        public async Task<FichaAtendimentoDto> GetAsync(Guid estabelecimentoId)
        {
            var restaurant = await _restaurant.GetAsync(estabelecimentoId)
                ?? throw new DeliveryDomainException(404, "ESTABELECIMENTO_NOT_FOUND", "Estabelecimento nao encontrado.");
            var categorias = await _cardapio.ListarCategoriasAsync(estabelecimentoId, null, true, 1, 500);
            var produtos = await _cardapio.ListarProdutosParaFichaAsync(estabelecimentoId);
            var atendimento = await _atendimento.ListAsync(estabelecimentoId);

            return CardapioFichaBuilder.Build(restaurant, categorias.Itens, produtos, atendimento, DateTimeOffset.UtcNow);
        }

        public async Task<ProdutoAtendimentoDto> GetAtendimentoAsync(Guid estabelecimentoId, Guid produtoId) =>
            await _atendimento.GetAsync(estabelecimentoId, produtoId) ?? new ProdutoAtendimentoDto();

        public async Task<ProdutoAtendimentoDto> SaveAtendimentoAsync(Guid estabelecimentoId, Guid produtoId, ProdutoAtendimentoDto data)
        {
            var normalized = CardapioFichaBuilder.Normalize(data);
            return await _atendimento.UpsertAsync(estabelecimentoId, produtoId, normalized)
                ?? throw new DeliveryDomainException(404, "PRODUTO_NOT_FOUND", "Produto nao encontrado neste estabelecimento.");
        }
    }
}
