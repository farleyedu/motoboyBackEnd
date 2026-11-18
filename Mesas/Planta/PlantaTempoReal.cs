using APIBack.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace APIBack.Mesas.Planta;

/// <summary>
/// Eventos da planta no grupo da loja do DeliveryHub (CONTRATO-API §4.2). Sem dado sensível: quem ouve relê a leitura.
/// Quando o P07 criar <c>IMesasTempoReal</c>, pode trocar a publicação por ela; os nomes dos eventos ficam.
/// </summary>
public interface IPlantaTempoReal
{
    Task SalaoMudou(Guid loja, int versaoPlanta);
    Task MesaMudou(Guid loja, Guid mesaId, int versao);
}

public sealed class PlantaTempoReal(IHubContext<DeliveryHub> hub, ILogger<PlantaTempoReal> log) : IPlantaTempoReal
{
    public const string SalaoEvento = "mesas.salao.mudou";
    public const string MesaEvento = "mesas.mesa.mudou";

    public Task SalaoMudou(Guid loja, int versaoPlanta) => Enviar(loja, SalaoEvento, new { versaoPlanta });

    public Task MesaMudou(Guid loja, Guid mesaId, int versao) => Enviar(loja, MesaEvento, new { mesaId, versao });

    private async Task Enviar(Guid loja, string evento, object payload)
    {
        try
        {
            await hub.Clients.Group(DeliveryRealtimeEvents.EstablishmentGroup(loja)).SendAsync(evento, payload);
        }
        catch (Exception ex)
        {
            // Já foi gravado; quem perdeu o aviso relê pelo polling de 15 s.
            log.LogWarning(ex, "Falha ao avisar {Evento} na loja {Loja}", evento, loja);
        }
    }
}
