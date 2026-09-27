using System.Collections.Generic;
using APIBack.DTOs.Delivery;

namespace APIBack.Service
{
    /// <summary>
    /// Reaproveita o mesmo calculo de distancia/taxa/zona do nucleo de pedido (OrderCoreRules) para
    /// um endereco hipotetico. A previsao de tempo e uma estimativa (preparo do estabelecimento +
    /// deslocamento a ~20 km/h em linha reta, com folga de 30%) - nunca dado real de entrega; a tela
    /// "Impacto operacional" e que mostra medias reais do historico.
    /// </summary>
    public static class DeliverySlaSimulator
    {
        private const double AvgSpeedKmh = 20d;

        public static SimuladorSlaResultDto Simular(
            RestaurantSettingsDto restaurant,
            IReadOnlyList<DeliveryZonaDto> zonas,
            SimularSlaRequest request,
            bool abertoAgora)
        {
            double? distance = null;
            var hasStore = restaurant.Latitude.HasValue && restaurant.Longitude.HasValue
                && !(restaurant.Latitude.Value == 0 && restaurant.Longitude.Value == 0);
            if (hasStore)
            {
                distance = OrderCoreRules.DistanceKm(restaurant.Latitude!.Value, restaurant.Longitude!.Value, request.Latitude, request.Longitude);
            }

            var dentroDoRaio = true;
            if (hasStore && restaurant.RaioEntregaKm is { } raio && raio > 0 && distance.HasValue)
            {
                dentroDoRaio = distance.Value <= (double)raio;
            }

            var zona = OrderCoreRules.ResolveZone(zonas, distance);
            var taxa = OrderCoreRules.ComputeFee(restaurant, distance, zonas);

            var preparoMin = restaurant.TempoPreparoMin ?? 20;
            var deslocamentoMin = distance.HasValue ? (distance.Value / AvgSpeedKmh) * 60d : 10d;
            var minMinutos = preparoMin + (int)System.Math.Round(deslocamentoMin * 0.85);
            var maxMinutos = preparoMin + (int)System.Math.Round(deslocamentoMin * 1.35) + 5;

            return new SimuladorSlaResultDto
            {
                ZonaNome = zona?.Nome,
                DistanciaKm = distance,
                DentroDoRaio = dentroDoRaio,
                TaxaEntrega = taxa,
                PrevisaoMinMinutos = minMinutos,
                PrevisaoMaxMinutos = maxMinutos,
                EstabelecimentoAbertoAgora = abertoAgora,
            };
        }
    }
}
