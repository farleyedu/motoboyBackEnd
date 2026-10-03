using System;
using System.Collections.Generic;

namespace APIBack.Service
{
    /// <summary>
    /// Confere o ponto de entrega que o cliente confirmou no mapa. Nao decide cobertura de entrega (isso e da loja): so
    /// barra o que e absurdo (ponto fora do Brasil ou a centenas de km da loja).
    /// </summary>
    public static class PontoDeEntregaRules
    {
        public const string Geocodificada = "geocodificada";
        public const string Pino = "pino";
        public const string Gps = "gps";
        private const double RaioPadraoKm = 60;
        private const double FolgaDoRaio = 2.0;

        public static readonly IReadOnlyList<string> Origens = new[] { Geocodificada, Pino, Gps };

        public static bool NoBrasil(double latitude, double longitude) =>
            double.IsFinite(latitude) && double.IsFinite(longitude)
            && latitude is >= -34.0 and <= 6.0
            && longitude is >= -74.5 and <= -28.0;

        /// <summary>Distancia em km entre dois pontos (haversine).</summary>
        public static double DistanciaKm(double lat1, double lng1, double lat2, double lng2)
        {
            const double raioDaTerraKm = 6371.0;
            static double Rad(double g) => g * Math.PI / 180.0;
            var dLat = Rad(lat2 - lat1);
            var dLng = Rad(lng2 - lng1);
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                    + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
            return 2 * raioDaTerraKm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }

        /// <summary>Mensagem de erro, ou nulo quando o ponto e aceitavel.</summary>
        public static string? Validar(double? latitude, double? longitude, string? origem, double? lojaLatitude, double? lojaLongitude, decimal? raioKm)
        {
            if (!latitude.HasValue || !longitude.HasValue) return "Confirme o ponto de entrega no mapa.";
            if (string.IsNullOrWhiteSpace(origem) || !Origens.Contains(origem.Trim().ToLowerInvariant())) return "Origem do ponto de entrega invalida.";
            if (!NoBrasil(latitude.Value, longitude.Value)) return "O ponto de entrega esta fora do Brasil.";

            if (lojaLatitude.HasValue && lojaLongitude.HasValue && NoBrasil(lojaLatitude.Value, lojaLongitude.Value))
            {
                var limite = (raioKm is > 0 ? (double)raioKm.Value : RaioPadraoKm) * FolgaDoRaio;
                if (DistanciaKm(latitude.Value, longitude.Value, lojaLatitude.Value, lojaLongitude.Value) > Math.Max(limite, 5))
                {
                    return "Esse ponto fica longe demais da loja. Confira onde colocou o pino.";
                }
            }

            return null;
        }
    }
}
