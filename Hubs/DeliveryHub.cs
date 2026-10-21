using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Extensions;
using APIBack.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
        /// <summary>A Gestao mudou a situacao comercial da loja (bloqueio etc.): o painel recarrega a situacao e, se bloqueado, mostra "conta suspensa".</summary>
        public const string EstabelecimentoSituacaoAlterada = "estabelecimento.situacao.alterada";

        public static string EstablishmentGroup(Guid estabelecimentoId) => $"establishment:{estabelecimentoId:N}";
        public static string SessionGroup(Guid sessionId) => $"delivery-session:{sessionId:N}";
    }

    /// <summary>Conexoes locais que devem cair imediatamente quando a Gestao bloqueia uma loja.</summary>
    public sealed class DeliveryHubConnections
    {
        private readonly ConcurrentDictionary<string, (Guid Store, HubCallerContext Context)> _connections = new();

        public void Add(string id, Guid store, HubCallerContext context) => _connections[id] = (store, context);
        public void Remove(string id) => _connections.TryRemove(id, out _);
        public Guid[] Stores() => _connections.Values.Select(v => v.Store).Distinct().ToArray();
        public void Abort(Guid store)
        {
            foreach (var entry in _connections)
                if (entry.Value.Store == store) entry.Value.Context.Abort();
        }
    }

    /// <summary>Desconecta tambem as conexoes de outras instancias, onde a chamada da Gestao nao passou.</summary>
    public sealed class DeliveryHubBlockWorker : BackgroundService
    {
        private readonly DeliveryHubConnections _connections;
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<DeliveryHubBlockWorker> _logger;

        public DeliveryHubBlockWorker(DeliveryHubConnections connections, IServiceScopeFactory scopes, ILogger<DeliveryHubBlockWorker> logger)
        {
            _connections = connections;
            _scopes = scopes;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    var situacoes = scope.ServiceProvider.GetRequiredService<ISituacaoComercialService>();
                    foreach (var store in _connections.Stores())
                    {
                        var situacao = await situacoes.ObterAsync(store, stoppingToken);
                        if (situacao?.Situacao == SituacoesComerciais.Bloqueado) _connections.Abort(store);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogWarning(ex, "Falha ao verificar conexoes de lojas bloqueadas."); }
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public class DeliveryHub : Hub
    {
        private readonly ISituacaoComercialService _situacoes;
        private readonly DeliveryHubConnections _connections;

        public DeliveryHub(ISituacaoComercialService situacoes, DeliveryHubConnections connections)
        {
            _situacoes = situacoes;
            _connections = connections;
        }

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

            var situacao = await _situacoes.ObterAsync(estabelecimentoId.Value, httpContext!.RequestAborted);
            if (situacao?.Situacao == SituacoesComerciais.Bloqueado)
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
                _connections.Add(Context.ConnectionId, estabelecimentoId.Value, Context);
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

            _connections.Add(Context.ConnectionId, estabelecimentoId.Value, Context);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _connections.Remove(Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }
    }
}
