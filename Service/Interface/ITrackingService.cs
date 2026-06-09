using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Tracking;

namespace APIBack.Service.Interface
{
    public interface ITrackingService
    {
        Task<MotoboyStatusRealtimeDto> SetStatusAsync(int userId, Guid estabelecimentoId, MotoboyStatusRequest request);
        Task<MotoboyStatusRealtimeDto> SetSimulatorStatusAsync(Guid estabelecimentoId, int motoboyId, MotoboyStatusRequest request);
        Task<MotoboyLocationResult> ReceiveLocationAsync(int userId, Guid estabelecimentoId, MotoboyLocationRequest request, bool allowSimulatorJump = false);
        Task<MotoboyLocationResult> ReceiveSimulatorLocationAsync(Guid estabelecimentoId, int motoboyId, MotoboyLocationRequest request);
        Task<MotoboyLocationBatchResult> ReceiveLocationBatchAsync(int userId, Guid estabelecimentoId, MotoboyLocationBatchRequest request);
        Task<MotoboyMapDto> CreateSimulatorMotoboyAsync(Guid estabelecimentoId, CreateSimulatorMotoboyRequest request);
        Task<SimulatorMotoboySessionResponse> StartSimulatorSessionAsync(Guid estabelecimentoId, int motoboyId);
        Task<DeliveryMapStateDto> GetMapStateAsync(Guid estabelecimentoId);
        Task<IReadOnlyCollection<MotoboyLocationHistoryPointDto>> GetLocationHistoryAsync(Guid estabelecimentoId, int motoboyId, DateOnly localDate);
    }
}
