using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Tracking;
using APIBack.Model.Auth;
using APIBack.Model.Tracking;
using APIBack.Options;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;
using Microsoft.Extensions.Options;

namespace APIBack.Service
{
    public sealed class OperationalSessionService : IOperationalSessionService
    {
        private readonly IOperationalSessionRepository _repository;
        private readonly IJwtService _jwtService;
        private readonly DeliveryTrackingOptions _options;

        public OperationalSessionService(
            IOperationalSessionRepository repository,
            IJwtService jwtService,
            IOptions<DeliveryTrackingOptions> options)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _jwtService = jwtService ?? throw new ArgumentNullException(nameof(jwtService));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        }

        public async Task<OperationalSessionTokenResponse> StartMobileSessionAsync(
            int userId,
            Guid estabelecimentoId,
            StartOperationalSessionRequest request)
        {
            EnsureEnabled();
            if (request == null)
            {
                throw new DeliveryDomainException(400, "INVALID_REQUEST", "Corpo da requisicao obrigatorio.");
            }

            EnsureAttempt(request.AttemptId);
            var session = await _repository.StartMobileSessionAsync(
                userId,
                estabelecimentoId,
                request.AttemptId,
                request.ClientInstanceId ?? request.AttemptId.ToString("D"));
            return CreateTokenResponse<OperationalSessionTokenResponse>(session);
        }

        public async Task<OperationalSessionTokenResponse> SwitchMobileSessionAsync(
            int userId,
            SwitchOperationalSessionRequest request)
        {
            EnsureEnabled();
            if (request == null)
            {
                throw new DeliveryDomainException(400, "INVALID_REQUEST", "Corpo da requisicao obrigatorio.");
            }

            EnsureAttempt(request.AttemptId);
            if (!request.Confirm || request.ExpectedSessionId == Guid.Empty || request.TargetEstablishmentId == Guid.Empty)
            {
                throw new DeliveryDomainException(
                    422,
                    "SWITCH_CONFIRMATION_REQUIRED",
                    "Troca de estabelecimento exige sessao esperada, destino e confirmacao explicita.");
            }

            var session = await _repository.StartMobileSessionAsync(
                userId,
                request.TargetEstablishmentId,
                request.AttemptId,
                request.ClientInstanceId ?? request.AttemptId.ToString("D"),
                request.ExpectedSessionId,
                explicitSwitch: true);
            return CreateTokenResponse<OperationalSessionTokenResponse>(session);
        }

        public async Task<SimulatorAutoStartResponse> AutoStartSimulatorSessionAsync(
            int actorUserId,
            Guid estabelecimentoId,
            StartSimulatorSessionRequest request)
        {
            EnsureEnabled();
            if (!_options.SimulatorEnabled)
            {
                throw new DeliveryDomainException(503, "SIMULATOR_DISABLED", "Simulador operacional desabilitado.");
            }

            if (request == null)
            {
                throw new DeliveryDomainException(400, "INVALID_REQUEST", "Corpo da requisicao obrigatorio.");
            }

            EnsureAttempt(request.AttemptId);
            var session = await _repository.StartSimulatorSessionAsync(actorUserId, estabelecimentoId, request.AttemptId);
            return CreateTokenResponse<SimulatorAutoStartResponse>(session);
        }

        public async Task<OperationalHeartbeatResponse> HeartbeatAsync(JwtPayload payload)
        {
            EnsureEnabled();
            var context = RequireOperationalContext(payload);
            var session = await _repository.HeartbeatAsync(context.SessionId, context.MotoboyId, context.SessionEpoch);
            var token = GenerateOperationalToken(session);
            return new OperationalHeartbeatResponse
            {
                AccessToken = token,
                ExpiresIn = OperationalTokenExpiresInSeconds,
                ServerTimeUtc = DateTimeOffset.UtcNow,
                Session = MapSession(session)
            };
        }

        public async Task<OperationalSessionDto> EndSessionAsync(JwtPayload payload, string reason)
        {
            EnsureEnabled();
            var context = RequireOperationalContext(payload);
            var session = await _repository.EndSessionAsync(
                context.SessionId,
                context.MotoboyId,
                context.SessionEpoch,
                reason);
            return MapSession(session);
        }

        public async Task<OperationalLocationAckDto> ReceiveLocationAsync(
            JwtPayload payload,
            OperationalLocationRequest request)
        {
            EnsureEnabled();
            var context = RequireOperationalContext(payload);
            var location = DeliveryTrackingPolicy.ValidateAndMap(request, _options, DateTimeOffset.UtcNow);
            var result = await _repository.WriteLocationAsync(
                context.SessionId,
                context.MotoboyId,
                context.SessionEpoch,
                location);
            return new OperationalLocationAckDto
            {
                SampleId = location.SampleId,
                Sequence = location.Sequence,
                Outcome = result.Outcome,
                UpdatedCurrent = result.UpdatedCurrent,
                SessionVersion = result.SessionVersion,
                ReceivedAtUtc = result.ReceivedAtUtc
            };
        }

        public async Task<OperationalSessionDto?> GetSessionAsync(JwtPayload payload)
        {
            EnsureEnabled();
            var context = RequireOperationalContext(payload);
            var session = await _repository.GetSessionAsync(context.SessionId);
            if (session == null || session.MotoboyId != context.MotoboyId || session.SessionEpoch != context.SessionEpoch)
            {
                return null;
            }

            return MapSession(session);
        }

        public Task<DeliveryTrackingSnapshotDto> GetSnapshotAsync(Guid estabelecimentoId)
        {
            EnsureEnabled();
            return _repository.GetSnapshotAsync(estabelecimentoId);
        }

        public Task<IReadOnlyCollection<SimulatorCandidateDto>> GetSimulatorCandidatesAsync(Guid estabelecimentoId)
        {
            EnsureEnabled();
            if (!_options.SimulatorEnabled)
            {
                throw new DeliveryDomainException(503, "SIMULATOR_DISABLED", "Simulador operacional desabilitado.");
            }

            return _repository.GetSimulatorCandidatesAsync(estabelecimentoId);
        }

        public async Task<MotoboyMapDto> CreateSimulatorMotoboyAsync(
            Guid estabelecimentoId,
            CreateSimulatorMotoboyRequest request)
        {
            EnsureEnabled();
            if (!_options.SimulatorEnabled)
            {
                throw new DeliveryDomainException(503, "SIMULATOR_DISABLED", "Simulador operacional desabilitado.");
            }

            var nome = string.IsNullOrWhiteSpace(request?.Nome) ? "Motoboy Simulado" : request.Nome.Trim();
            if (nome.Length > 120)
            {
                throw new DeliveryDomainException(422, "INVALID_SIMULATOR_MOTOBOY", "Nome excede 120 caracteres.");
            }

            var telefone = string.IsNullOrWhiteSpace(request?.Telefone) ? null : request.Telefone.Trim();
            var motoboy = await _repository.CreateSimulatorMotoboyAsync(estabelecimentoId, nome, telefone);
            return new MotoboyMapDto
            {
                Id = motoboy.MotoboyId,
                Nome = motoboy.Nome,
                Avatar = motoboy.Avatar,
                Status = "offline"
            };
        }

        private T CreateTokenResponse<T>(OperationalSessionRecord session)
            where T : OperationalSessionTokenResponse, new()
        {
            return new T
            {
                EstablishmentId = session.EstabelecimentoId,
                AccessToken = GenerateOperationalToken(session),
                ExpiresIn = OperationalTokenExpiresInSeconds,
                Motoboy = new MotoboyMapDto
                {
                    Id = session.MotoboyId,
                    Nome = session.Nome,
                    Avatar = session.Avatar,
                    Status = "online"
                },
                Session = MapSession(session)
            };
        }

        private string GenerateOperationalToken(OperationalSessionRecord session)
        {
            var actorUserId = session.StartedByUserId ?? session.UsuarioId
                ?? throw new DeliveryDomainException(500, "SESSION_ACTOR_MISSING", "Sessao sem autor autenticavel.");
            var payload = new JwtPayload
            {
                UserId = actorUserId,
                Nome = session.Nome,
                Email = session.Origin == "simulator"
                    ? $"simulator-{actorUserId}@local.invalid"
                    : string.Empty,
                IsSuperAdmin = false,
                EstabelecimentoId = session.EstabelecimentoId,
                TipoAcesso = "motoboy",
                Permissoes = new Dictionary<string, List<string>>(),
                MotoboySessionId = session.SessionId,
                MotoboyId = session.MotoboyId,
                ClientType = session.Origin,
                TokenUse = "delivery_operational",
                SessionEpoch = session.SessionEpoch,
                Scope = "delivery:tracking"
            };
            return _jwtService.GenerateToken(payload, TimeSpan.FromMinutes(_options.OperationalTokenExpirationMinutes));
        }

        private OperationalSessionDto MapSession(OperationalSessionRecord session) => new()
        {
            SessionId = session.SessionId,
            Epoch = session.SessionEpoch,
            Origin = session.Origin,
            StartedAtUtc = session.StartedAtUtc,
            PresenceExpiresAtUtc = session.ExpiresAtUtc,
            HeartbeatIntervalSeconds = _options.HeartbeatIntervalSeconds,
            Version = session.Version,
            IsEnded = session.EndedAtUtc.HasValue,
            EndedAtUtc = session.EndedAtUtc,
            EndReason = session.EndReason
        };

        private void EnsureEnabled()
        {
            if (!_options.Enabled)
            {
                throw new DeliveryDomainException(503, "DELIVERY_TRACKING_DISABLED", "Tracking operacional ainda nao foi ativado.");
            }
        }

        private static void EnsureAttempt(Guid attemptId)
        {
            if (attemptId == Guid.Empty)
            {
                throw new DeliveryDomainException(422, "ATTEMPT_ID_REQUIRED", "attemptId e obrigatorio.");
            }
        }

        private static (Guid SessionId, int MotoboyId, long SessionEpoch) RequireOperationalContext(JwtPayload payload)
        {
            if (!string.Equals(payload.TokenUse, "delivery_operational", StringComparison.Ordinal) ||
                !payload.MotoboySessionId.HasValue ||
                !payload.MotoboyId.HasValue ||
                !payload.SessionEpoch.HasValue)
            {
                throw new DeliveryDomainException(401, "OPERATIONAL_TOKEN_REQUIRED", "Token operacional obrigatorio.");
            }

            return (payload.MotoboySessionId.Value, payload.MotoboyId.Value, payload.SessionEpoch.Value);
        }

        private int OperationalTokenExpiresInSeconds =>
            checked(Math.Max(1, _options.OperationalTokenExpirationMinutes) * 60);
    }
}
