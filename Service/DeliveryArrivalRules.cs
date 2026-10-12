using System;

namespace APIBack.Service
{
    internal static class DeliveryArrivalRules
    {
        public static void EnsurePickedUp(DateTimeOffset? pickedUpAtUtc)
        {
            if (!pickedUpAtUtc.HasValue)
                throw new DeliveryDomainException(409, "PICKUP_REQUIRED", "Confirme a retirada e a saída da loja antes de registrar a chegada.");
        }

        public static void EnsureCompletionStage(DateTimeOffset? pickedUpAtUtc, DateTimeOffset? arrivedAtUtc)
        {
            EnsurePickedUp(pickedUpAtUtc);
            if (!arrivedAtUtc.HasValue)
                throw new DeliveryDomainException(409, "ARRIVAL_REQUIRED", "Confirme a chegada ao cliente antes de conferir a entrega.");
        }

        public static void EnsureExpectedPedido(int? expectedPedidoId, int currentPedidoId)
        {
            if (expectedPedidoId.HasValue && expectedPedidoId.Value != currentPedidoId)
                throw new DeliveryDomainException(409, "CURRENT_DELIVERY_CHANGED", "A entrega atual mudou. Confira sua rota antes de registrar a chegada.");
        }
    }
}
