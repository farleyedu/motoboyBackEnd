using System;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;

namespace APIBack.Repository.Interface
{
    public interface IRestaurantSettingsRepository
    {
        /// <summary>Null quando o estabelecimento nao existe.</summary>
        Task<RestaurantSettingsDto?> GetAsync(Guid estabelecimentoId);

        /// <summary>Null quando o estabelecimento nao existe.</summary>
        Task<RestaurantSettingsDto?> UpdateAsync(Guid estabelecimentoId, UpdateRestaurantSettingsRequest request);
    }
}
