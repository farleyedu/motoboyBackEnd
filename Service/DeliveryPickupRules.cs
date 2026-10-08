using APIBack.DTOs.Delivery;

namespace APIBack.Service;

internal static class DeliveryPickupRules
{
    internal static void ValidateRequest(PickupStopsRequest request)
    {
        if (request.ExpectedPedidoId <= 0 || request.ExpectedVersion < 0 || request.PedidoIds == null || request.PedidoIds.Count is < 1 or > 100 || request.PedidoIds.Any(id => id <= 0) || request.PedidoIds.Distinct().Count() != request.PedidoIds.Count || !request.PedidoIds.Contains(request.ExpectedPedidoId))
            throw new DeliveryDomainException(400, "INVALID_PICKUP", "Confira os pedidos da retirada.");
    }
    internal static void ValidateSnapshot(PickupStopsRequest request, long currentVersion, IReadOnlyCollection<int> activeIds, bool alreadyPickedUp)
    {
        if (!activeIds.ToHashSet().SetEquals(request.PedidoIds)) throw new DeliveryDomainException(409, "QUEUE_MISMATCH", "Os pedidos mudaram. Confira novamente a retirada.");
        if (currentVersion != request.ExpectedVersion && !alreadyPickedUp) throw new DeliveryDomainException(409, "QUEUE_VERSION_CONFLICT", "A fila mudou. Confira novamente a retirada.");
    }
}
