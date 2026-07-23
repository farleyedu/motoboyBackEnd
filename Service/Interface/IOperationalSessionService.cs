using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Tracking;
using APIBack.Model.Auth;

namespace APIBack.Service.Interface
{
    public interface IOperationalSessionService
    {
        Task<OperationalSessionTokenResponse> StartMobileSessionAsync(
            int userId,
            Guid estabelecimentoId,
            StartOperationalSessionRequest request);

        Task<OperationalSessionTokenResponse> SwitchMobileSessionAsync(
            int userId,
            SwitchOperationalSessionRequest request);

        Task<SimulatorAutoStartResponse> AutoStartSimulatorSessionAsync(
            int actorUserId,
            Guid estabelecimentoId,
            StartSimulatorSessionRequest request);

        Task<OperationalHeartbeatResponse> HeartbeatAsync(JwtPayload payload);
        Task<OperationalSessionDto> EndSessionAsync(JwtPayload payload, string reason);
        Task<OperationalLocationAckDto> ReceiveLocationAsync(JwtPayload payload, OperationalLocationRequest request);
        Task<OperationalSessionDto?> GetSessionAsync(JwtPayload payload);
        Task<DeliveryTrackingSnapshotDto> GetSnapshotAsync(Guid estabelecimentoId);
        Task<IReadOnlyCollection<SimulatorCandidateDto>> GetSimulatorCandidatesAsync(Guid estabelecimentoId);
        Task<MotoboyMapDto> CreateSimulatorMotoboyAsync(Guid estabelecimentoId, CreateSimulatorMotoboyRequest request);
    }
}

