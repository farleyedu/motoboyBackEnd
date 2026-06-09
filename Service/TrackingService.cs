using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Tracking;
using APIBack.Hubs;
using APIBack.Model.Auth;
using APIBack.Model.Enum;
using APIBack.Model.Tracking;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace APIBack.Service
{
    public class TrackingService : ITrackingService
    {
        private const double MaxAcceptedSpeedMps = 70;
        private readonly ITrackingRepository _repository;
        private readonly IHubContext<DeliveryHub> _hubContext;
        private readonly IJwtService _jwtService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<TrackingService> _logger;

        public TrackingService(
            ITrackingRepository repository,
            IHubContext<DeliveryHub> hubContext,
            IJwtService jwtService,
            IConfiguration configuration,
            ILogger<TrackingService> logger)
        {
            _repository = repository;
            _hubContext = hubContext;
            _jwtService = jwtService;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<MotoboyStatusRealtimeDto> SetStatusAsync(int userId, Guid estabelecimentoId, MotoboyStatusRequest request)
        {
            var identity = await ResolveIdentityAsync(userId, estabelecimentoId);
            return await SetStatusForIdentityAsync(identity, estabelecimentoId, request, userId);
        }

        public async Task<MotoboyStatusRealtimeDto> SetSimulatorStatusAsync(
            Guid estabelecimentoId,
            int motoboyId,
            MotoboyStatusRequest request)
        {
            var identity = await ResolveMotoboyIdentityAsync(estabelecimentoId, motoboyId);
            return await SetStatusForIdentityAsync(identity, estabelecimentoId, request, null);
        }

        public async Task<MotoboyMapDto> CreateSimulatorMotoboyAsync(
            Guid estabelecimentoId,
            CreateSimulatorMotoboyRequest request)
        {
            var identity = await _repository.CreateSimulatorMotoboyAsync(
                estabelecimentoId,
                request.Nome,
                request.Telefone);

            await PublishAsync(DeliveryRealtimeEvents.DeliveryOrderUpdated, estabelecimentoId, new
            {
                MotoboyId = identity.MotoboyId,
                EstabelecimentoId = estabelecimentoId,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });

            return new MotoboyMapDto
            {
                Id = identity.MotoboyId,
                Nome = identity.Nome,
                Avatar = identity.Avatar,
                Status = "offline"
            };
        }

        public async Task<SimulatorMotoboySessionResponse> StartSimulatorSessionAsync(
            Guid estabelecimentoId,
            int motoboyId)
        {
            var identity = await ResolveMotoboyIdentityAsync(estabelecimentoId, motoboyId);
            var sessionId = await _repository.StartMotoboySessionAsync(identity, estabelecimentoId, "simulator");
            var userId = identity.UsuarioId ?? identity.MotoboyId;
            var expirationMinutes = int.TryParse(_configuration["Jwt:ExpirationMinutes"], out var minutes)
                ? minutes
                : 60;

            var payload = new JwtPayload
            {
                UserId = userId,
                Nome = identity.Nome,
                Email = $"motoboy-{identity.MotoboyId}@simulator.local",
                IsSuperAdmin = false,
                EstabelecimentoId = estabelecimentoId,
                TipoAcesso = "motoboy",
                MotoboySessionId = sessionId,
                MotoboyId = identity.MotoboyId,
                ClientType = "simulator",
                Permissoes = new Dictionary<string, List<string>>()
            };

            return new SimulatorMotoboySessionResponse
            {
                AccessToken = _jwtService.GenerateToken(payload),
                ExpiresIn = expirationMinutes * 60,
                SessionId = sessionId,
                Motoboy = new MotoboyMapDto
                {
                    Id = identity.MotoboyId,
                    Nome = identity.Nome,
                    Avatar = identity.Avatar,
                    Status = ToStatusLabel(identity.Status)
                }
            };
        }

        private async Task<MotoboyStatusRealtimeDto> SetStatusForIdentityAsync(
            MotoboyTrackingIdentity identity,
            Guid estabelecimentoId,
            MotoboyStatusRequest request,
            int? userId)
        {
            var normalizedStatus = NormalizeStatus(request.Status);
            var trackingMode = NormalizeTrackingMode(request.TrackingMode);
            var statusCode = ToStatusCode(normalizedStatus, trackingMode);

            if (userId.HasValue)
            {
                await _repository.SetMotoboyStatusAsync(identity.MotoboyId, userId.Value, estabelecimentoId, statusCode);
            }
            else
            {
                await _repository.SetMotoboyStatusAsync(identity.MotoboyId, estabelecimentoId, statusCode);
            }

            var evt = new MotoboyStatusRealtimeDto
            {
                MotoboyId = identity.MotoboyId,
                EstabelecimentoId = estabelecimentoId,
                Status = normalizedStatus,
                TrackingMode = trackingMode,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            await PublishAsync(DeliveryRealtimeEvents.MotoboyStatusChanged, estabelecimentoId, evt);
            return evt;
        }

        public Task<MotoboyLocationResult> ReceiveLocationAsync(int userId, Guid estabelecimentoId, MotoboyLocationRequest request)
        {
            return ProcessLocationAsync(userId, estabelecimentoId, request, allowHistoricalStale: false);
        }

        public async Task<MotoboyLocationResult> ReceiveSimulatorLocationAsync(
            Guid estabelecimentoId,
            int motoboyId,
            MotoboyLocationRequest request)
        {
            var validationError = ValidateLocationRequest(request);
            if (validationError != null)
            {
                return Reject(validationError);
            }

            var identity = await ResolveMotoboyIdentityAsync(estabelecimentoId, motoboyId);
            return await ProcessLocationForIdentityAsync(identity, estabelecimentoId, request, allowHistoricalStale: false);
        }

        public async Task<MotoboyLocationBatchResult> ReceiveLocationBatchAsync(
            int userId,
            Guid estabelecimentoId,
            MotoboyLocationBatchRequest request)
        {
            var result = new MotoboyLocationBatchResult();
            var locations = request.Locations
                .OrderBy(l => l.ClientTimestampUtc ?? DateTimeOffset.UtcNow)
                .Take(100)
                .ToList();

            foreach (var location in locations)
            {
                var item = await ProcessLocationAsync(userId, estabelecimentoId, location, allowHistoricalStale: true);
                result.Results.Add(item);

                if (item.Accepted)
                {
                    result.Accepted++;
                }
                else
                {
                    result.Rejected++;
                }
            }

            return result;
        }

        public Task<DeliveryMapStateDto> GetMapStateAsync(Guid estabelecimentoId)
        {
            return _repository.GetMapStateAsync(estabelecimentoId);
        }

        public Task<IReadOnlyCollection<MotoboyLocationHistoryPointDto>> GetLocationHistoryAsync(
            Guid estabelecimentoId,
            int motoboyId,
            DateOnly localDate)
        {
            return _repository.GetLocationHistoryAsync(estabelecimentoId, motoboyId, localDate);
        }

        private async Task<MotoboyLocationResult> ProcessLocationAsync(
            int userId,
            Guid estabelecimentoId,
            MotoboyLocationRequest request,
            bool allowHistoricalStale)
        {
            var validationError = ValidateLocationRequest(request);
            if (validationError != null)
            {
                return Reject(validationError);
            }

            var identity = await ResolveIdentityAsync(userId, estabelecimentoId);
            return await ProcessLocationForIdentityAsync(identity, estabelecimentoId, request, allowHistoricalStale);
        }

        private async Task<MotoboyLocationResult> ProcessLocationForIdentityAsync(
            MotoboyTrackingIdentity identity,
            Guid estabelecimentoId,
            MotoboyLocationRequest request,
            bool allowHistoricalStale)
        {
            var previousState = await _repository.GetLocationStateAsync(identity.MotoboyId);
            var trackingMode = NormalizeTrackingMode(request.TrackingMode);
            var clientTimestamp = (request.ClientTimestampUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
            var serverTimestamp = DateTimeOffset.UtcNow;
            var localDate = await ResolveLocalDateAsync(estabelecimentoId, clientTimestamp);

            if (IsOutOfOrder(previousState, request.Sequence, clientTimestamp))
            {
                if (allowHistoricalStale && clientTimestamp.Date == DateTimeOffset.UtcNow.Date)
                {
                    await MaybeInsertHistoryAsync(identity, request, trackingMode, "stale", clientTimestamp, serverTimestamp, localDate);
                    return new MotoboyLocationResult
                    {
                        Accepted = true,
                        UpdatedCurrentState = false,
                        Reason = "historical_only"
                    };
                }

                return Reject("out_of_order");
            }

            if (IsDuplicateOrFlood(previousState, request, clientTimestamp))
            {
                return Reject("throttled");
            }

            if (IsImplausibleJump(previousState, request, clientTimestamp))
            {
                _logger.LogWarning(
                    "Rejected implausible motoboy location. MotoboyId={MotoboyId} Lat={Latitude} Lng={Longitude}",
                    identity.MotoboyId,
                    request.Latitude,
                    request.Longitude);
                return Reject("implausible_jump");
            }

            var quality = ResolveQuality(request);
            var state = new MotoboyLocationState
            {
                MotoboyId = identity.MotoboyId,
                EstabelecimentoId = estabelecimentoId,
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                AccuracyMeters = request.AccuracyMeters,
                SpeedMps = request.SpeedMps,
                HeadingDegrees = request.HeadingDegrees,
                TrackingMode = trackingMode,
                Quality = quality,
                LastSequence = request.Sequence,
                ClientTimestampUtc = clientTimestamp,
                ServerReceivedAtUtc = serverTimestamp,
                UpdatedAt = serverTimestamp
            };

            await _repository.UpsertLocationStateAsync(identity, state);
            await MaybeInsertHistoryAsync(identity, request, trackingMode, quality, clientTimestamp, serverTimestamp, localDate);
            await _repository.CleanupOldHistoryAsync(localDate.AddDays(-1));

            var evt = new MotoboyRealtimeDto
            {
                MotoboyId = identity.MotoboyId,
                EstabelecimentoId = estabelecimentoId,
                Nome = identity.Nome,
                Status = trackingMode == "active_route" ? "delivering" : "online",
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                AccuracyMeters = request.AccuracyMeters,
                SpeedMps = request.SpeedMps,
                HeadingDegrees = request.HeadingDegrees,
                TrackingMode = trackingMode,
                Quality = quality,
                ClientTimestampUtc = clientTimestamp,
                ServerReceivedAtUtc = serverTimestamp,
                Sequence = request.Sequence
            };

            await PublishAsync(DeliveryRealtimeEvents.MotoboyLocationUpdated, estabelecimentoId, evt);

            return new MotoboyLocationResult
            {
                Accepted = true,
                UpdatedCurrentState = true,
                Location = evt
            };
        }

        private async Task<MotoboyTrackingIdentity> ResolveIdentityAsync(int userId, Guid estabelecimentoId)
        {
            var identity = await _repository.ResolveMotoboyForUserAsync(userId, estabelecimentoId);
            if (identity == null)
            {
                throw new UnauthorizedAccessException("Usuario autenticado nao possui vinculo ativo com motoboy neste estabelecimento.");
            }

            return identity;
        }

        private async Task<MotoboyTrackingIdentity> ResolveMotoboyIdentityAsync(Guid estabelecimentoId, int motoboyId)
        {
            var identity = await _repository.ResolveMotoboyByIdAsync(motoboyId, estabelecimentoId);
            if (identity == null)
            {
                throw new UnauthorizedAccessException("Motoboy nao encontrado ou nao vinculado ao estabelecimento ativo.");
            }

            return identity;
        }

        private static string? ValidateLocationRequest(MotoboyLocationRequest request)
        {
            if (request.Latitude is < -90 or > 90)
            {
                return "invalid_latitude";
            }

            if (request.Longitude is < -180 or > 180)
            {
                return "invalid_longitude";
            }

            if (request.AccuracyMeters.HasValue && request.AccuracyMeters.Value < 0)
            {
                return "invalid_accuracy";
            }

            return null;
        }

        private static bool IsOutOfOrder(MotoboyLocationState? previous, long? sequence, DateTimeOffset clientTimestamp)
        {
            if (previous == null)
            {
                return false;
            }

            if (previous.LastSequence.HasValue && sequence.HasValue && sequence.Value <= previous.LastSequence.Value)
            {
                return true;
            }

            if (!sequence.HasValue && clientTimestamp <= previous.ClientTimestampUtc.AddSeconds(-5))
            {
                return true;
            }

            return sequence.HasValue &&
                   previous.LastSequence.HasValue &&
                   sequence.Value < previous.LastSequence.Value &&
                   clientTimestamp <= previous.ClientTimestampUtc;
        }

        private static bool IsDuplicateOrFlood(MotoboyLocationState? previous, MotoboyLocationRequest request, DateTimeOffset clientTimestamp)
        {
            if (previous == null)
            {
                return false;
            }

            var seconds = Math.Abs((clientTimestamp - previous.ClientTimestampUtc).TotalSeconds);
            var distance = HaversineMeters(previous.Latitude, previous.Longitude, request.Latitude, request.Longitude);

            return seconds < 1 && distance < 3;
        }

        private static bool IsImplausibleJump(MotoboyLocationState? previous, MotoboyLocationRequest request, DateTimeOffset clientTimestamp)
        {
            if (previous == null)
            {
                return false;
            }

            var seconds = Math.Max(1, (clientTimestamp - previous.ClientTimestampUtc).TotalSeconds);
            if (seconds <= 0)
            {
                return false;
            }

            var distance = HaversineMeters(previous.Latitude, previous.Longitude, request.Latitude, request.Longitude);
            return distance > 100 && distance / seconds > MaxAcceptedSpeedMps;
        }

        private async Task MaybeInsertHistoryAsync(
            MotoboyTrackingIdentity identity,
            MotoboyLocationRequest request,
            string trackingMode,
            string quality,
            DateTimeOffset clientTimestamp,
            DateTimeOffset serverTimestamp,
            DateOnly localDate)
        {
            if (request.AccuracyMeters.HasValue && request.AccuracyMeters.Value > 1000)
            {
                return;
            }

            var lastPoint = await _repository.GetLastHistoryPointAsync(identity.MotoboyId, localDate);
            var shouldSave = lastPoint == null;

            if (!shouldSave)
            {
                var distance = HaversineMeters(lastPoint!.Latitude, lastPoint.Longitude, request.Latitude, request.Longitude);
                var seconds = Math.Abs((clientTimestamp - lastPoint.ClientTimestampUtc).TotalSeconds);

                if (trackingMode == "active_route")
                {
                    shouldSave = distance >= 15 || seconds >= 60;
                }
                else
                {
                    shouldSave = distance >= 50 || seconds >= 120;
                }
            }

            if (!shouldSave)
            {
                return;
            }

            await _repository.InsertHistoryPointAsync(new MotoboyLocationHistoryPoint
            {
                MotoboyId = identity.MotoboyId,
                EstabelecimentoId = identity.EstabelecimentoId,
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                AccuracyMeters = request.AccuracyMeters,
                SpeedMps = request.SpeedMps,
                HeadingDegrees = request.HeadingDegrees,
                TrackingMode = trackingMode,
                Quality = quality,
                ClientTimestampUtc = clientTimestamp,
                ServerReceivedAtUtc = serverTimestamp,
                LocalDate = localDate,
                Sequence = request.Sequence
            });
        }

        private async Task<DateOnly> ResolveLocalDateAsync(Guid estabelecimentoId, DateTimeOffset timestampUtc)
        {
            var timezone = await _repository.GetEstablishmentTimezoneAsync(estabelecimentoId);

            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
                return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestampUtc, tz).DateTime);
            }
            catch
            {
                // America/Sao_Paulo sem DST na regra operacional atual.
                return DateOnly.FromDateTime(timestampUtc.ToOffset(TimeSpan.FromHours(-3)).DateTime);
            }
        }

        private Task PublishAsync(string eventName, Guid estabelecimentoId, object payload)
        {
            return _hubContext
                .Clients
                .Group(DeliveryRealtimeEvents.EstablishmentGroup(estabelecimentoId))
                .SendAsync(eventName, payload);
        }

        private static MotoboyLocationResult Reject(string reason)
        {
            return new MotoboyLocationResult
            {
                Accepted = false,
                UpdatedCurrentState = false,
                Reason = reason
            };
        }

        private static string NormalizeTrackingMode(string? value)
        {
            var normalized = value?.Trim().ToLowerInvariant();
            return normalized == "active_route" ? "active_route" : "online_idle";
        }

        private static string NormalizeStatus(string? value)
        {
            var normalized = value?.Trim().ToLowerInvariant();
            return normalized switch
            {
                "offline" => "offline",
                "entregando" => "delivering",
                "delivering" => "delivering",
                "active_route" => "delivering",
                _ => "online"
            };
        }

        private static int ToStatusCode(string status, string trackingMode)
        {
            if (status == "offline")
            {
                return (int)StatusMotoboy.Offline;
            }

            if (status == "delivering" || trackingMode == "active_route")
            {
                return (int)StatusMotoboy.Entregando;
            }

            return (int)StatusMotoboy.Online;
        }

        private static string ToStatusLabel(int statusCode)
        {
            return statusCode switch
            {
                (int)StatusMotoboy.Online => "online",
                (int)StatusMotoboy.Entregando => "delivering",
                _ => "offline"
            };
        }

        private static string ResolveQuality(MotoboyLocationRequest request)
        {
            if (!request.AccuracyMeters.HasValue)
            {
                return "unknown";
            }

            return request.AccuracyMeters.Value <= 100 ? "good" : "low";
        }

        private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
        {
            const double radius = 6371000;
            var dLat = ToRadians(lat2 - lat1);
            var dLon = ToRadians(lon2 - lon1);
            var a =
                Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return radius * c;
        }

        private static double ToRadians(double value) => value * Math.PI / 180;
    }
}
