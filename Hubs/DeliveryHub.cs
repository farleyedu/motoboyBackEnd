using System;
using System.Threading.Tasks;
using APIBack.Extensions;
using Microsoft.AspNetCore.SignalR;

namespace APIBack.Hubs
{
    public static class DeliveryRealtimeEvents
    {
        public const string MotoboyLocationUpdated = "motoboy.location.updated";
        public const string MotoboyStatusChanged = "motoboy.status.changed";
        public const string DeliveryOrderUpdated = "delivery.order.updated";
        public const string DeliveryRouteAssigned = "delivery.route.assigned";

        public static string EstablishmentGroup(Guid estabelecimentoId) => $"establishment:{estabelecimentoId:N}";
    }

    public class DeliveryHub : Hub
    {
        public override async Task OnConnectedAsync()
        {
            var httpContext = Context.GetHttpContext();
            var userId = httpContext?.GetUserId();
            var estabelecimentoId = httpContext?.GetEstabelecimentoId();

            if (!userId.HasValue || !estabelecimentoId.HasValue || estabelecimentoId.Value == Guid.Empty)
            {
                Context.Abort();
                return;
            }

            var canViewDelivery = httpContext!.IsSuperAdmin() || httpContext.TemPermissao("Delivery", "visualizar");
            if (!canViewDelivery)
            {
                Context.Abort();
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, DeliveryRealtimeEvents.EstablishmentGroup(estabelecimentoId.Value));
            await base.OnConnectedAsync();
        }
    }
}
