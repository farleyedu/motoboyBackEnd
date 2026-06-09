using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Tracking;
using APIBack.Model.Tracking;

namespace APIBack.Repository.Interface
{
    public interface ITrackingRepository
    {
        Task EnsureSchemaAsync();
        Task<MotoboyTrackingIdentity?> ResolveMotoboyForUserAsync(int userId, Guid estabelecimentoId);
        Task<MotoboyTrackingIdentity?> ResolveMotoboyByIdAsync(int motoboyId, Guid estabelecimentoId);
        Task<MotoboyTrackingIdentity> CreateSimulatorMotoboyAsync(Guid estabelecimentoId, string nome, string? telefone);
        Task<Guid> StartMotoboySessionAsync(MotoboyTrackingIdentity identity, Guid estabelecimentoId, string deviceType);
        Task SetMotoboyStatusAsync(int motoboyId, int userId, Guid estabelecimentoId, int status);
        Task SetMotoboyStatusAsync(int motoboyId, Guid estabelecimentoId, int status);
        Task<MotoboyLocationState?> GetLocationStateAsync(int motoboyId);
        Task UpsertLocationStateAsync(MotoboyTrackingIdentity identity, MotoboyLocationState state);
        Task<MotoboyLocationHistoryPoint?> GetLastHistoryPointAsync(int motoboyId, DateOnly localDate);
        Task InsertHistoryPointAsync(MotoboyLocationHistoryPoint point);
        Task CleanupOldHistoryAsync(DateOnly keepFromLocalDate);
        Task<string> GetEstablishmentTimezoneAsync(Guid estabelecimentoId);
        Task<DeliveryMapStateDto> GetMapStateAsync(Guid estabelecimentoId);
        Task<IReadOnlyCollection<MotoboyLocationHistoryPointDto>> GetLocationHistoryAsync(Guid estabelecimentoId, int motoboyId, DateOnly localDate);
    }
}
