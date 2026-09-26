using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Tracking;
using APIBack.Model.Tracking;

namespace APIBack.Repository.Interface
{
    public interface IOperationalSessionRepository
    {
        Task<OperationalSessionRecord> StartMobileSessionAsync(
            int userId,
            Guid estabelecimentoId,
            Guid attemptId,
            string clientInstanceId,
            Guid? expectedSessionId = null,
            bool explicitSwitch = false);

        Task<OperationalSessionRecord> StartSimulatorSessionAsync(
            int actorUserId,
            Guid estabelecimentoId,
            Guid attemptId,
            int? motoboyId);

        Task<OperationalSessionRecord?> GetSessionAsync(Guid sessionId);
        Task<OperationalSessionRecord> HeartbeatAsync(Guid sessionId, int motoboyId, long sessionEpoch);
        Task<OperationalSessionRecord> EndSessionAsync(Guid sessionId, int motoboyId, long sessionEpoch, string reason);
        Task<OperationalLocationWriteResult> WriteLocationAsync(
            Guid sessionId,
            int motoboyId,
            long sessionEpoch,
            OperationalLocationWrite location);

        Task<DeliveryTrackingSnapshotDto> GetSnapshotAsync(Guid estabelecimentoId);
        Task<IReadOnlyCollection<SimulatorCandidateDto>> GetSimulatorCandidatesAsync(Guid estabelecimentoId);
        Task<OperationalMotoboyIdentity> CreateSimulatorMotoboyAsync(Guid estabelecimentoId, string nome, string? telefone);
        /// <summary>Renomeia/troca o telefone de um motoboy de teste do estabelecimento; false quando nao e de teste ou nao e dele.</summary>
        Task<bool> UpdateSimulatorMotoboyAsync(Guid estabelecimentoId, int motoboyId, string nome, string? telefone, string? avatar = null);
        /// <summary>Tira o motoboy de teste do estabelecimento (encerra sessoes). Recusa se ainda tem pedido ativo.</summary>
        Task<SimulatorMotoboyRemoval> RemoveSimulatorMotoboyAsync(Guid estabelecimentoId, int motoboyId);
        Task<bool> CanUserManageEstablishmentAsync(int userId, bool isSuperAdmin, Guid estabelecimentoId);
        Task<IReadOnlyCollection<MotoboyLocationHistoryPointDto>> GetTrajectoryAsync(
            Guid estabelecimentoId, int motoboyId, DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit);
        Task<int> ExpireDueSessionsAsync(int limit);
        Task<int> EndActiveMobileSessionsForUserAsync(int userId, string reason);
        Task<int> DeleteOldLocationsAsync(DateTimeOffset receivedBeforeUtc, int limit);
    }
}

