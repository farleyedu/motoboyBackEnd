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
            Guid attemptId);

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
        Task<int> ExpireDueSessionsAsync(int limit);
        Task<int> EndActiveMobileSessionsForUserAsync(int userId, string reason);
        Task<int> DeleteOldLocationsAsync(DateTimeOffset receivedBeforeUtc, int limit);
    }
}

