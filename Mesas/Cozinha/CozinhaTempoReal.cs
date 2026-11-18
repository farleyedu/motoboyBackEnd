using APIBack.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace APIBack.Mesas.Pedidos;

/// <summary><c>mesas.cozinha.mudou {estacaoIds[], pedidoId}</c> no grupo da loja do DeliveryHub (CONTRATO-API §4.2). Sem dado sensível.</summary>
public interface ICozinhaTempoReal
{
    Task CozinhaMudou(Guid loja, IEnumerable<string> estacaoIds, Guid? pedidoId);
}

public sealed class CozinhaTempoReal(IHubContext<DeliveryHub> hub, ILogger<CozinhaTempoReal> log) : ICozinhaTempoReal
{
    public const string Evento = "mesas.cozinha.mudou";

    public async Task CozinhaMudou(Guid loja, IEnumerable<string> estacaoIds, Guid? pedidoId)
    {
        try
        {
            await hub.Clients.Group(DeliveryRealtimeEvents.EstablishmentGroup(loja))
                .SendAsync(Evento, new { estacaoIds = estacaoIds.Distinct().ToArray(), pedidoId });
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Falha ao avisar a cozinha da loja {Loja}", loja);
        }
    }
}
