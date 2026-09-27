using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;

namespace APIBack.Repository.Interface
{
    public interface IDeliveryZonaRepository
    {
        Task<IReadOnlyList<DeliveryZonaDto>> ListAsync(Guid estabelecimentoId);
        /// <summary>So as ativas, ordenadas por raio (menor primeiro) - o que o calculo de taxa usa.</summary>
        Task<IReadOnlyList<DeliveryZonaDto>> ListAtivasOrdenadasAsync(Guid estabelecimentoId);
        Task<DeliveryZonaDto> CreateAsync(Guid estabelecimentoId, SalvarZonaRequest request);
        Task<DeliveryZonaDto?> UpdateAsync(Guid estabelecimentoId, Guid zonaId, SalvarZonaRequest request);
        Task<bool> DeleteAsync(Guid estabelecimentoId, Guid zonaId);
    }
}
