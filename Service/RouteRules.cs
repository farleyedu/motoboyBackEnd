using System;
using System.Collections.Generic;
using System.Linq;

namespace APIBack.Service
{
    public static class RouteStates
    {
        public const string Idle = "idle";
        public const string Returning = "returning";
    }

    /// <summary>Uma parada ativa da fila, na ordem atual.</summary>
    public sealed record RouteSlot(int PedidoId, bool Locked, bool EnRoute);

    /// <summary>
    /// Lock com posicao absoluta (D5): a parada travada e uma ancora no lugar em que esta na fila.
    /// Os demais pedidos so se reordenam nos espacos livres, sem tirar a ancora do lugar.
    /// </summary>
    public static class RouteLockRules
    {
        public const int MaxPedidosPerLockRequest = 100;

        /// <summary>Travar so vale para pedido que esta na fila (atribuido ou em rota).</summary>
        public static bool CanLock(string stopStatus) => stopStatus is "assigned" or "en_route";

        /// <summary>
        /// Valida a ordem pedida contra as ancoras. Pressupoe que <paramref name="requested"/> tem os
        /// mesmos pedidos de <paramref name="current"/> (isso e validado antes, com QUEUE_MISMATCH).
        /// </summary>
        public static void ValidateReorder(IReadOnlyList<RouteSlot> current, IReadOnlyList<int> requested)
        {
            if (current.Count != requested.Count)
            {
                throw new DeliveryDomainException(422, "QUEUE_MISMATCH",
                    "A lista enviada nao corresponde exatamente aos pedidos ativos na fila deste motoboy.");
            }

            for (var index = 0; index < current.Count; index++)
            {
                var slot = current[index];
                if (slot.Locked && requested[index] != slot.PedidoId)
                {
                    throw new DeliveryDomainException(409, "ORDER_LOCKED",
                        $"O pedido {slot.PedidoId} esta travado na posicao {index + 1} da fila e nao pode ser movido. " +
                        "So o estabelecimento pode destravar.",
                        new { pedidoId = slot.PedidoId, position = index + 1 });
                }
            }
        }

        /// <summary>Acao do motoboy sobre pedido travado (recusar, transferir).</summary>
        public static void EnsureNotLockedForMotoboy(bool locked, int pedidoId, string action)
        {
            if (locked)
            {
                throw new DeliveryDomainException(409, "ORDER_LOCKED",
                    $"O pedido {pedidoId} esta travado pelo estabelecimento e nao pode ser {action}.",
                    new { pedidoId });
            }
        }

        public static IReadOnlyList<int> NormalizeIds(IReadOnlyList<int>? pedidoIds)
        {
            if (pedidoIds == null || pedidoIds.Count == 0)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "Informe ao menos um pedido.");
            }
            if (pedidoIds.Any(id => id <= 0))
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "pedidoIds contem id invalido.");
            }
            var distinct = pedidoIds.Distinct().ToList();
            if (distinct.Count > MaxPedidosPerLockRequest)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST",
                    $"Informe no maximo {MaxPedidosPerLockRequest} pedidos por vez.");
            }
            return distinct;
        }
    }

    /// <summary>Retorno a loja: quando a rota entra em "retornando" e quando termina.</summary>
    public static class ReturnToStoreRules
    {
        public const int DefaultRadiusMeters = 80;
        public const int MinRadiusMeters = 10;
        public const int MaxRadiusMeters = 2000;

        /// <summary>A rota so passa a "retornando" quando o estabelecimento exige e nao sobrou parada.</summary>
        public static bool ShouldEnterReturning(bool requireReturn, int activeStopsLeft) =>
            requireReturn && activeStopsLeft == 0;

        public static int NormalizeRadius(int? radiusMeters)
        {
            if (!radiusMeters.HasValue) return DefaultRadiusMeters;
            if (radiusMeters.Value is < MinRadiusMeters or > MaxRadiusMeters)
            {
                throw new DeliveryDomainException(422, "INVALID_STORE_RETURN_RADIUS",
                    $"O raio de retorno a loja deve ficar entre {MinRadiusMeters} e {MaxRadiusMeters} metros.");
            }
            return radiusMeters.Value;
        }

        /// <summary>Dentro do raio da loja. Sem coordenada da loja ou do motoboy nunca conta como dentro.</summary>
        public static bool IsInsideStore(double? latitude, double? longitude, double? storeLatitude, double? storeLongitude, int radiusMeters)
        {
            if (!latitude.HasValue || !longitude.HasValue || !storeLatitude.HasValue || !storeLongitude.HasValue) return false;
            if (double.IsNaN(latitude.Value) || double.IsNaN(longitude.Value)) return false;
            var meters = OrderCoreRules.DistanceKm(latitude.Value, longitude.Value, storeLatitude.Value, storeLongitude.Value) * 1000d;
            return meters <= radiusMeters;
        }
    }
}
