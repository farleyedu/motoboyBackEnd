using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Tracking;
using APIBack.Model.Auth;
using APIBack.Model.Tracking;
using APIBack.Options;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;
using APIBack.Services;
using Microsoft.Extensions.Options;

namespace APIBack.Service
{
    public sealed class OperationalSessionService : IOperationalSessionService
    {
        private readonly IOperationalSessionRepository _repository;
        private readonly IJwtService _jwtService;
        private readonly DeliveryTrackingOptions _options;
        private readonly IPedidoQueueRepository _queueRepository;
        private readonly DeliverySyncMetrics? _metrics;

        public OperationalSessionService(
            IOperationalSessionRepository repository,
            IJwtService jwtService,
            IOptions<DeliveryTrackingOptions> options,
            IPedidoQueueRepository queueRepository, DeliverySyncMetrics? metrics = null)
        {
            _metrics = metrics;
            _queueRepository = queueRepository ?? throw new ArgumentNullException(nameof(queueRepository));
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
            if (request.MotoboyId.HasValue && request.MotoboyId.Value <= 0)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "motoboyId invalido.");
            }

            var session = await _repository.StartSimulatorSessionAsync(
                actorUserId, estabelecimentoId, request.AttemptId, request.MotoboyId);
            var response = CreateTokenResponse<SimulatorAutoStartResponse>(session);
            response.NextLocationSequence = Math.Max(1, session.NextLocationSequence);
            return response;
        }

        public async Task<OperationalHeartbeatResponse> HeartbeatAsync(JwtPayload payload, CancellationToken cancellationToken = default)
        {
            EnsureEnabled();
            var context = RequireOperationalContext(payload);
            var session = await _repository.HeartbeatAsync(context.SessionId, context.MotoboyId, context.SessionEpoch, cancellationToken);
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
            OperationalLocationRequest request, CancellationToken cancellationToken = default)
        {
            EnsureEnabled();
            var context = RequireOperationalContext(payload);
            var location = DeliveryTrackingPolicy.ValidateAndMap(request, _options, DateTimeOffset.UtcNow);
            var result = await _repository.WriteLocationAsync(
                context.SessionId,
                context.MotoboyId,
                context.SessionEpoch,
                location, cancellationToken);
            if (result.Outcome == "accepted" && result.UpdatedCurrent && payload.EstabelecimentoId.HasValue &&
                DeliveryTrackingPolicy.IsLocationFresh(location.CapturedAtUtc, DateTimeOffset.UtcNow, _options.LocationFreshnessSeconds))
            {
                // Retorno a loja: se a rota esta "retornando" e a posicao entrou no raio da loja, encerra.
                // Nunca lanca (a posicao ja foi aceita).
                await _queueRepository.TryFinishReturnByLocationAsync(
                    payload.EstabelecimentoId.Value, context.MotoboyId, location.Latitude, location.Longitude);
            }
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

        public async Task<OperationalLocationBatchAckDto> ReceiveLocationsAsync(
            JwtPayload payload, OperationalLocationBatchRequest request, CancellationToken cancellationToken = default)
        {
            EnsureEnabled();
            var context = RequireOperationalContext(payload);
            var samples = OperationalLocationBatchRules.ValidateShape(request);
            var now = DateTimeOffset.UtcNow;
            var locations = new List<OperationalLocationWrite>();
            var validIndices = new List<int>();
            var acknowledgments = new OperationalLocationAckDto[samples.Count];
            for (var i = 0; i < samples.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    locations.Add(DeliveryTrackingPolicy.ValidateAndMap(samples[i], _options, now));
                    validIndices.Add(i);
                }
                catch (DeliveryDomainException ex) when (ex.Code == "LOCATION_INVALID")
                {
                    _metrics?.RecordLocations("rejected", 1);
                    acknowledgments[i] = new OperationalLocationAckDto { SampleId = samples[i].SampleId!.Value,
                        Sequence = samples[i].Sequence, Outcome = "rejected", Code = ex.Code, ReceivedAtUtc = now };
                }
            }
            if (locations.Count > 0)
            {
                var results = await _repository.WriteLocationsAsync(context.SessionId, context.MotoboyId,
                    context.SessionEpoch, locations, cancellationToken);
                for (var i = 0; i < locations.Count; i++)
                {
                    var location = locations[i];
                    var result = results[i];
                    acknowledgments[validIndices[i]] = new OperationalLocationAckDto { SampleId = location.SampleId,
                        Sequence = location.Sequence, Outcome = result.Outcome, UpdatedCurrent = result.UpdatedCurrent,
                        SessionVersion = result.SessionVersion, ReceivedAtUtc = result.ReceivedAtUtc };
                    if (result.Outcome == "accepted" && result.UpdatedCurrent && payload.EstabelecimentoId.HasValue &&
                        DeliveryTrackingPolicy.IsLocationFresh(location.CapturedAtUtc, now, _options.LocationFreshnessSeconds))
                        await _queueRepository.TryFinishReturnByLocationAsync(payload.EstabelecimentoId.Value,
                            context.MotoboyId, location.Latitude, location.Longitude);
                }
            }
            return new OperationalLocationBatchAckDto { Samples = acknowledgments };
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
            int actorUserId,
            bool isSuperAdmin,
            Guid activeEstabelecimentoId,
            CreateSimulatorMotoboyRequest request)
        {
            EnsureEnabled();
            if (!_options.SimulatorEnabled)
            {
                throw new DeliveryDomainException(503, "SIMULATOR_DISABLED", "Simulador operacional desabilitado.");
            }

            // O operador escolhe o estabelecimento do motoboy. O ativo ja foi autorizado
            // pela permissao da rota; outro precisa estar entre os vinculos do usuario.
            var estabelecimentoId = request?.EstabelecimentoId is Guid escolhido && escolhido != Guid.Empty
                ? escolhido
                : activeEstabelecimentoId;
            if (estabelecimentoId != activeEstabelecimentoId
                && !await _repository.CanUserManageEstablishmentAsync(actorUserId, isSuperAdmin, estabelecimentoId))
            {
                throw new DeliveryDomainException(403, "ESTABLISHMENT_FORBIDDEN",
                    "Voce nao tem acesso ao estabelecimento escolhido.");
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

        public async Task UpdateSimulatorMotoboyAsync(Guid estabelecimentoId, int motoboyId, UpdateSimulatorMotoboyRequest request)
        {
            EnsureEnabled();
            if (!_options.SimulatorEnabled)
            {
                throw new DeliveryDomainException(503, "SIMULATOR_DISABLED", "Simulador operacional desabilitado.");
            }

            var nome = request?.Nome?.Trim();
            if (string.IsNullOrEmpty(nome))
            {
                throw new DeliveryDomainException(422, "INVALID_SIMULATOR_MOTOBOY", "Informe o nome do motoboy.");
            }
            if (nome.Length > 120)
            {
                throw new DeliveryDomainException(422, "INVALID_SIMULATOR_MOTOBOY", "Nome excede 120 caracteres.");
            }
            var telefone = string.IsNullOrWhiteSpace(request?.Telefone) ? null : request!.Telefone!.Trim();
            if (telefone is { Length: > 30 })
            {
                throw new DeliveryDomainException(422, "INVALID_SIMULATOR_MOTOBOY", "Telefone excede 30 caracteres.");
            }

            var avatar = request?.Avatar?.Trim();
            if (avatar is { Length: > 300_000 })
            {
                throw new DeliveryDomainException(422, "INVALID_SIMULATOR_MOTOBOY", "Foto grande demais.");
            }

            if (!await _repository.UpdateSimulatorMotoboyAsync(estabelecimentoId, motoboyId, nome, telefone, avatar))
            {
                throw new DeliveryDomainException(404, "SIMULATOR_MOTOBOY_NOT_FOUND",
                    "Motoboy de teste nao encontrado neste estabelecimento.");
            }
        }

        public async Task RemoveSimulatorMotoboyAsync(Guid estabelecimentoId, int motoboyId)
        {
            EnsureEnabled();
            if (!_options.SimulatorEnabled)
            {
                throw new DeliveryDomainException(503, "SIMULATOR_DISABLED", "Simulador operacional desabilitado.");
            }

            var result = await _repository.RemoveSimulatorMotoboyAsync(estabelecimentoId, motoboyId);
            if (result == SimulatorMotoboyRemoval.NotFound)
            {
                throw new DeliveryDomainException(404, "SIMULATOR_MOTOBOY_NOT_FOUND",
                    "Motoboy de teste nao encontrado neste estabelecimento.");
            }
            if (result == SimulatorMotoboyRemoval.HasActiveOrders)
            {
                throw new DeliveryDomainException(409, "MOTOBOY_HAS_ACTIVE_ORDERS",
                    "Este motoboy ainda tem pedido atribuido ou em rota. Transfira ou conclua antes de remover.");
            }
        }

        public Task<IReadOnlyCollection<MotoboyLocationHistoryPointDto>> GetTrajectoryAsync(
            Guid estabelecimentoId, int motoboyId, DateOnly localDate)
        {
            EnsureEnabled();
            if (motoboyId <= 0)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "motoboyId invalido.");
            }

            var (fromUtc, toUtc) = OperationalDayWindow.ToUtcRange(localDate);
            return _repository.GetTrajectoryAsync(estabelecimentoId, motoboyId, fromUtc, toUtc, TrajectoryPointLimit);
        }

        private const int TrajectoryPointLimit = 5000;

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
                    Status = "online",
                    Latitude = session.LastKnownLatitude,
                    Longitude = session.LastKnownLongitude
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
            NextLocationSequence = session.NextLocationSequence,
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
