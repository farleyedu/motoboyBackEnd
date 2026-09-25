using System;
using System.Collections.Generic;
using APIBack.DTOs.Delivery;
using APIBack.Model.Delivery;

namespace APIBack.Service
{
    /// <summary>Resultado das regras do estabelecimento para um pedido.</summary>
    public sealed class StoreRulesResult
    {
        /// <summary>Taxa de entrega a cobrar (fixa + por km, ou a informada quando a origem permite).</summary>
        public decimal DeliveryFee { get; init; }
        /// <summary>Distancia em linha reta loja-endereco; null sem coordenada da loja.</summary>
        public double? DistanceKm { get; init; }
        /// <summary>Regras que nao bloquearam (origens em que o atendente decide).</summary>
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Regras do estabelecimento aplicadas no servidor (aceita pedidos, pedido minimo, raio e taxa).
    /// Pura, sem banco: recebe as configuracoes ja carregadas. Nas origens estritas (cardapio web e
    /// IA) as violacoes bloqueiam; nas demais viram avisos, para nao mudar o fluxo do atendente.
    /// </summary>
    public static class OrderCoreRules
    {
        public const decimal MaxMoney = 100_000m;

        public static StoreRulesResult Evaluate(
            RestaurantSettingsDto settings,
            decimal subtotal,
            double latitude,
            double longitude,
            PedidoOrigem.OriginRules rules,
            decimal? feeOverride)
        {
            var warnings = new List<string>();
            void Violation(int status, string code, string message)
            {
                if (rules.EnforceStoreRules) throw new DeliveryDomainException(status, code, message);
                warnings.Add(message);
            }

            if (!settings.AceitaPedidos)
            {
                Violation(409, "STORE_NOT_ACCEPTING", "O estabelecimento nao esta aceitando pedidos no momento.");
            }

            var minimum = settings.PedidoMinimo ?? 0m;
            if (minimum > 0 && subtotal < minimum)
            {
                Violation(422, "MINIMUM_NOT_REACHED",
                    $"Pedido minimo de {minimum:0.00} nao atingido (subtotal {subtotal:0.00}).");
            }

            double? distance = null;
            if (HasStorePosition(settings))
            {
                distance = DistanceKm(settings.Latitude!.Value, settings.Longitude!.Value, latitude, longitude);
                var radius = settings.RaioEntregaKm ?? 0m;
                if (radius > 0 && distance.Value > (double)radius)
                {
                    Violation(422, "OUT_OF_DELIVERY_RANGE",
                        $"Endereco fora do raio de entrega ({distance.Value:0.0} km, maximo {radius:0.0} km).");
                }
            }

            decimal fee;
            if (feeOverride.HasValue && rules.AllowFeeOverride)
            {
                if (feeOverride.Value < 0 || feeOverride.Value > MaxMoney)
                {
                    throw new DeliveryDomainException(422, "INVALID_ORDER", "Taxa de entrega invalida.");
                }
                fee = decimal.Round(feeOverride.Value, 2);
            }
            else
            {
                fee = ComputeFee(settings, distance);
            }

            return new StoreRulesResult { DeliveryFee = fee, DistanceKm = distance, Warnings = warnings };
        }

        /// <summary>Taxa fixa + taxa por km x distancia (so quando ha distancia). Arredondada a 2 casas.</summary>
        public static decimal ComputeFee(RestaurantSettingsDto settings, double? distanceKm)
        {
            var fee = settings.TaxaEntregaFixa ?? 0m;
            var perKm = settings.TaxaEntregaPorKm ?? 0m;
            if (perKm > 0 && distanceKm.HasValue)
            {
                fee += perKm * (decimal)distanceKm.Value;
            }
            return decimal.Round(fee, 2);
        }

        private static bool HasStorePosition(RestaurantSettingsDto settings) =>
            settings.Latitude.HasValue && settings.Longitude.HasValue
            && double.IsFinite(settings.Latitude.Value) && double.IsFinite(settings.Longitude.Value)
            && !(settings.Latitude.Value == 0 && settings.Longitude.Value == 0);

        /// <summary>Distancia em linha reta (haversine), em km.</summary>
        public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
        {
            static double Rad(double value) => value * Math.PI / 180d;
            const double earthRadiusKm = 6371d;
            var dLat = Rad(lat2 - lat1);
            var dLon = Rad(lon2 - lon1);
            var a = Math.Pow(Math.Sin(dLat / 2), 2)
                + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Pow(Math.Sin(dLon / 2), 2);
            return 2 * earthRadiusKm * Math.Asin(Math.Min(1d, Math.Sqrt(a)));
        }

        private static readonly Dictionary<string, string> PaymentAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["dinheiro"] = "dinheiro",
            ["pix"] = "pix",
            ["cartao_entrega"] = "cartao_entrega",
            ["cartao na entrega"] = "cartao_entrega",
            ["cartao"] = "cartao_entrega",
            ["link"] = "link",
            ["link de pagamento"] = "link",
            ["outro"] = "outro",
        };

        /// <summary>
        /// Forma de pagamento informativa (D10): valores conhecidos sao normalizados; texto livre
        /// legado (ex.: "pagoApp", "Dinheiro") e preservado como veio, sem quebrar o painel.
        /// </summary>
        public static string? NormalizePayment(string? value)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            var key = RemoveAccents(trimmed);
            return PaymentAliases.TryGetValue(key, out var normalized) ? normalized : trimmed;
        }

        private static string RemoveAccents(string value)
        {
            var normalized = value.Normalize(System.Text.NormalizationForm.FormD);
            var builder = new System.Text.StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(ch);
                }
            }
            return builder.ToString().Normalize(System.Text.NormalizationForm.FormC);
        }
    }
}
