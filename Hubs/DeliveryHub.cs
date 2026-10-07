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
        public const string DeliveryQueueUpdated = "delivery.queue.updated";
        public const string DeliveryTransferUpdated = "delivery.transfer.updated";
        /// <summary>A rota do motoboy entrou em "retornando a loja".</summary>
        public const string DeliveryRouteReturning = "delivery.route.returning";
        /// <summary>O motoboy chegou a loja (raio ou acao manual): rota encerrada.</summary>
        public const string DeliveryRouteReturned = "delivery.route.returned";
        /// <summary>Mensagem interna atendente <-> motoboy presa ao pedido (Fase 5).</summary>
        public const string DeliveryMotoboyMessage = "delivery.motoboy.message";
        public const string MotoboyLinkRequested = "motoboy.link.requested";

        public static string EstablishmentGroup(Guid estabelecimentoId) => $"establishment:{estabelecimentoId:N}";
        public static string SessionGroup(Guid sessionId) => $"delivery-session:{sessionId:N}";
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

            var payload = httpContext!.GetJwtPayload();
            if (string.Equals(payload.TokenUse, "delivery_operational", StringComparison.Ordinal) &&
                payload.MotoboySessionId.HasValue)
            {
                await Groups.AddToGroupAsync(
                    Context.ConnectionId,
                    DeliveryRealtimeEvents.SessionGroup(payload.MotoboySessionId.Value));
                await base.OnConnectedAsync();
                return;
            }

            var canViewDelivery = httpContext!.IsSuperAdmin() || httpContext.TemPermissao("Delivery", "visualizar") || httpContext.TemPermissao("Delivery", "gestao_motoboy");
            var canViewChat = httpContext.IsSuperAdmin() || httpContext.TemPermissao("WhatsApp", "visualizar");
            if (!canViewDelivery && !canViewChat)
            {
                Context.Abort();
                return;
            }

            // Cada grupo exige a sua permissao: quem so ve o delivery nao recebe o texto das conversas, e vice-versa.
            if (canViewDelivery)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, DeliveryRealtimeEvents.EstablishmentGroup(estabelecimentoId.Value));
            }

            if (canViewChat)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, APIBack.Atendimento.ChatRealtimeEvents.Group(estabelecimentoId.Value));
            }

            await base.OnConnectedAsync();
        }
    }
}
