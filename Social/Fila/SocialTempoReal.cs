using APIBack.Hubs;
using APIBack.Social.Editor;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace APIBack.Social.Fila;

public interface ISocialTempoReal
{
    Task RenderAtualizado(Guid loja, RenderDto render, CancellationToken ct);

    /// <summary>Só o id: cada pessoa busca a lista de novo e vê apenas as máquinas dela e as compartilhadas.</summary>
    Task MaquinaAtualizada(Guid loja, long maquinaId, CancellationToken ct);
}

/// <summary>Melhor esforço: falha aqui nunca desfaz nada; a tela relê de tempos em tempos.</summary>
public sealed class SocialTempoReal(IHubContext<DeliveryHub> hub, ILogger<SocialTempoReal> logger) : ISocialTempoReal
{
    public const string EventoRender = "social.render.atualizado";
    public const string EventoMaquina = "social.maquina.atualizada";

    public static string Grupo(Guid loja) => $"social:{loja:N}";

    public Task RenderAtualizado(Guid loja, RenderDto render, CancellationToken ct) => Enviar(loja, EventoRender, render, ct);

    public Task MaquinaAtualizada(Guid loja, long maquinaId, CancellationToken ct) => Enviar(loja, EventoMaquina, new { id = maquinaId }, ct);

    private async Task Enviar(Guid loja, string evento, object corpo, CancellationToken ct)
    {
        try { await hub.Clients.Group(Grupo(loja)).SendAsync(evento, corpo, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[social.rt] ev=erro evento={Evento} loja={Loja}", evento, loja);
        }
    }
}
