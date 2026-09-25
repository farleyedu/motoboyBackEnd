using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace APIBack.Service
{
    public static class NoticeTypes
    {
        public const string Dispatch = "saiu_da_loja";
        public const string Arriving = "motoboy_chegando";

        public static bool IsValid(string? type) => type == Dispatch || type == Arriving;
    }

    public static class NoticeStatuses
    {
        public const string Pending = "pendente";
        public const string Sent = "enviada";
        public const string Failed = "falhou";
        public const string Ignored = "ignorada";
    }

    /// <summary>Parametros dos avisos ao cliente por estabelecimento.</summary>
    public sealed class NoticeSettings
    {
        public const string DefaultDispatchTemplate =
            "Ola, {cliente}! Seu pedido {numero} saiu do {loja} com {motoboy}. Acompanhe a entrega: {link}";
        public const string DefaultArrivingTemplate =
            "{cliente}, seu pedido {numero} esta chegando: o motoboy chega em cerca de {minutos} min. Acompanhe: {link}";

        public bool DispatchEnabled { get; init; } = true;
        public bool ArrivingEnabled { get; init; } = true;
        public int ArrivingMinutes { get; init; } = 5;
        public int ArrivingRadiusM { get; init; } = 400;
        public string? TemplateDispatch { get; init; }
        public string? TemplateArriving { get; init; }

        public string DispatchText => string.IsNullOrWhiteSpace(TemplateDispatch) ? DefaultDispatchTemplate : TemplateDispatch!;
        public string ArrivingText => string.IsNullOrWhiteSpace(TemplateArriving) ? DefaultArrivingTemplate : TemplateArriving!;
    }

    public static class NoticeSettingsRules
    {
        public const int MinMinutes = 1, MaxMinutes = 60, MinRadius = 50, MaxRadius = 5000, MaxTemplate = 500;
        public static readonly string[] Variables = { "cliente", "numero", "loja", "motoboy", "minutos", "link" };
        private static readonly Regex Token = new(@"\{([a-zA-Z_]+)\}", RegexOptions.Compiled);

        public static int NormalizeMinutes(int? value)
        {
            if (!value.HasValue) return 5;
            if (value.Value is < MinMinutes or > MaxMinutes)
                throw new DeliveryDomainException(422, "INVALID_NOTICE_MINUTES", $"Os minutos do aviso devem ficar entre {MinMinutes} e {MaxMinutes}.");
            return value.Value;
        }

        public static int NormalizeRadius(int? value)
        {
            if (!value.HasValue) return 400;
            if (value.Value is < MinRadius or > MaxRadius)
                throw new DeliveryDomainException(422, "INVALID_NOTICE_RADIUS", $"O raio de reserva deve ficar entre {MinRadius} e {MaxRadius} metros.");
            return value.Value;
        }

        /// <summary>Texto vazio volta ao padrao (null); variavel desconhecida e recusada.</summary>
        public static string? NormalizeTemplate(string? template)
        {
            var text = template?.Trim();
            if (string.IsNullOrEmpty(text)) return null;
            if (text.Length > MaxTemplate)
                throw new DeliveryDomainException(422, "INVALID_NOTICE_TEMPLATE", $"O texto do aviso aceita no maximo {MaxTemplate} caracteres.");
            foreach (Match match in Token.Matches(text))
            {
                if (Array.IndexOf(Variables, match.Groups[1].Value.ToLowerInvariant()) < 0)
                {
                    throw new DeliveryDomainException(422, "INVALID_NOTICE_TEMPLATE",
                        $"Variavel desconhecida: {{{match.Groups[1].Value}}}. Use: {string.Join(", ", Array.ConvertAll(Variables, v => "{" + v + "}"))}.");
                }
            }
            return text;
        }
    }

    /// <summary>Estimativa de chegada calculada no servidor.</summary>
    public static class TrackingEta
    {
        public const double RoadFactor = 1.3;        // rua x linha reta
        public const double MinSpeedMps = 3.0;       // ~11 km/h: piso para nao estimar horas quando parado
        public const double MaxSpeedMps = 14.0;      // ~50 km/h
        public const double DefaultSpeedMps = 5.5;   // ~20 km/h de moto na cidade
        public const double UsableSpeedMps = 1.0;    // abaixo disso a velocidade medida nao e confiavel

        /// <summary>Minutos ate chegar. Sem velocidade confiavel usa a velocidade urbana padrao.</summary>
        public static double Minutes(double straightMeters, double? speedMps)
        {
            var speed = speedMps.HasValue && speedMps.Value >= UsableSpeedMps
                ? Math.Clamp(speedMps.Value, MinSpeedMps, MaxSpeedMps)
                : DefaultSpeedMps;
            return straightMeters * RoadFactor / speed / 60d;
        }
    }

    public enum ArrivingVerdict { Skip, NotYet, Qualifies }

    public sealed record ArrivingInput(
        bool MotoboyShares,
        double? DistanceMeters,
        double? LocationAgeSeconds,
        double? SpeedMps,
        NoticeSettings Settings);

    public static class ArrivingRules
    {
        /// <summary>Posicao mais velha que isso nao serve para decidir.</summary>
        public const double MaxLocationAgeSeconds = 120;

        /// <summary>
        /// Skip = nunca vai sair por este motivo (motoboy nao autorizou, sem posicao, posicao velha, aviso desligado);
        /// NotYet = ainda longe; Qualifies = dentro do tempo (ou do raio de reserva quando nao ha velocidade confiavel).
        /// </summary>
        public static (ArrivingVerdict Verdict, string? Reason) Evaluate(ArrivingInput input)
        {
            if (!input.Settings.ArrivingEnabled) return (ArrivingVerdict.Skip, "aviso_desligado");
            if (!input.MotoboyShares) return (ArrivingVerdict.Skip, "motoboy_nao_autorizou_localizacao");
            if (!input.DistanceMeters.HasValue || !input.LocationAgeSeconds.HasValue) return (ArrivingVerdict.Skip, "sem_posicao");
            if (input.LocationAgeSeconds.Value > MaxLocationAgeSeconds) return (ArrivingVerdict.Skip, "posicao_desatualizada");

            var reliableSpeed = input.SpeedMps.HasValue && input.SpeedMps.Value >= TrackingEta.UsableSpeedMps;
            var qualifies = reliableSpeed
                ? TrackingEta.Minutes(input.DistanceMeters.Value, input.SpeedMps) <= input.Settings.ArrivingMinutes
                : input.DistanceMeters.Value <= input.Settings.ArrivingRadiusM;
            return (qualifies ? ArrivingVerdict.Qualifies : ArrivingVerdict.NotYet, null);
        }
    }

    /// <summary>Evita disparar por uma unica amostra: o pedido precisa continuar qualificado por um tempo.</summary>
    public sealed class ArrivingHysteresis
    {
        private readonly TimeSpan _sustain;
        private readonly Dictionary<int, DateTimeOffset> _firstQualified = new();

        public ArrivingHysteresis(TimeSpan? sustain = null)
        {
            _sustain = sustain ?? TimeSpan.FromSeconds(20);
        }

        /// <summary>True quando o pedido esta qualificado sem interrupcao ha pelo menos o tempo minimo.</summary>
        public bool Observe(int pedidoId, bool qualifies, DateTimeOffset now)
        {
            if (!qualifies)
            {
                _firstQualified.Remove(pedidoId);
                return false;
            }
            if (!_firstQualified.TryGetValue(pedidoId, out var since))
            {
                _firstQualified[pedidoId] = now;
                return _sustain <= TimeSpan.Zero;
            }
            return now - since >= _sustain;
        }

        public void Forget(int pedidoId) => _firstQualified.Remove(pedidoId);
    }

    /// <summary>Token opaco do link publico de rastreio.</summary>
    public static class RastreioToken
    {
        public const int Bytes = 32;
        public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
        private static readonly Regex Shape = new(@"^[A-Za-z0-9_-]{43}$", RegexOptions.Compiled);

        public static string Generate() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(Bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static bool IsWellFormed(string? token) => token != null && Shape.IsMatch(token);
    }

    /// <summary>O que o cliente ve no link publico: campos minimos, posicao so com autorizacao do motoboy.</summary>
    public sealed class PublicTrackingView
    {
        public string Status { get; set; } = "em_preparo";
        public string StatusLabel { get; set; } = string.Empty;
        public bool Finalizado { get; set; }
        public string? Loja { get; set; }
        public int PedidoNumero { get; set; }
        public string? PrevisaoEntrega { get; set; }
        public string? MotoboyNome { get; set; }
        public double[]? PosicaoMotoboy { get; set; }
        public double[]? Destino { get; set; }
        public double[]? LojaPosicao { get; set; }
    }

    public static class PublicTrackingRules
    {
        public const double MaxPositionAgeSeconds = 300;

        public static (string Status, string Label, bool Final) StatusOf(int statusPedido) => statusPedido switch
        {
            3 => ("concluido", "Entrega finalizada", true),
            4 => ("cancelado", "Pedido cancelado", true),
            2 => ("em_rota", "A caminho", false),
            5 => ("com_motoboy", "Motoboy a caminho da loja", false),
            _ => ("em_preparo", "Pedido em preparo", false),
        };

        /// <summary>~110 m de precisao: suficiente para o mapa do cliente, sem expor a posicao exata de terceiros.</summary>
        public static double[]? Round(double? lat, double? lon) =>
            lat.HasValue && lon.HasValue ? new[] { Math.Round(lon.Value, 3), Math.Round(lat.Value, 3) } : null;

        /// <summary>Posicao do motoboy so quando ele autorizou, o pedido esta em rota e a posicao e recente.</summary>
        public static double[]? MotoboyPosition(bool shares, int statusPedido, double? lat, double? lon, double? ageSeconds)
        {
            if (!shares || statusPedido != 2 || !lat.HasValue || !lon.HasValue) return null;
            if (!ageSeconds.HasValue || ageSeconds.Value > MaxPositionAgeSeconds) return null;
            return new[] { Math.Round(lon.Value, 5), Math.Round(lat.Value, 5) };
        }
    }
}
