using System;

namespace APIBack.Service
{
    internal static class DeliveryArrivalRules
    {
        public static void EnsureExpectedPedido(int? expectedPedidoId, int currentPedidoId)
        {
            if (expectedPedidoId.HasValue && expectedPedidoId.Value != currentPedidoId)
                throw new DeliveryDomainException(409, "CURRENT_DELIVERY_CHANGED", "A entrega atual mudou. Confira sua rota antes de registrar a chegada.");
        }
    }
}
