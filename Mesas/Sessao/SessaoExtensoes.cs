using System.Text.Json.Nodes;
using APIBack.Hubs;
using APIBack.Mesas.Nucleo;
using Dapper;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace APIBack.Mesas.Sessao;

/// <summary>
/// Eventos do salão no grupo da loja do DeliveryHub (CONTRATO-API §4.2). Sem dado sensível: quem ouve relê a leitura.
/// Publicar só depois do commit. Outros prompts (P08, P14…) podem usar esta mesma interface.
/// </summary>
public interface IMesasTempoReal
{
    Task MesaMudou(Guid loja, Guid mesaId, int versao);
    Task PendenciasMudou(Guid loja, string tipo, Guid? garcomId);
    /// <summary><paramref name="tipo"/>: pronto, chamado, pedido, conta, cobertura, autorizacao, gorjeta, reserva.</summary>
    Task Aviso(Guid loja, string tipo, Guid id, Guid? mesaId, Guid? garcomId);
    Task SolicitacaoMudou(Guid loja, Guid id, string estado);
}

public sealed class MesasTempoReal(IHubContext<DeliveryHub> hub, ILogger<MesasTempoReal> log) : IMesasTempoReal
{
    public const string MesaEvento = "mesas.mesa.mudou", PendenciasEvento = "mesas.pendencias.mudou", AvisoEvento = "mesas.aviso",
        SolicitacaoEvento = "mesas.solicitacao.mudou";

    public Task MesaMudou(Guid loja, Guid mesaId, int versao) => Enviar(loja, MesaEvento, new { mesaId, versao });
    public Task PendenciasMudou(Guid loja, string tipo, Guid? garcomId) => Enviar(loja, PendenciasEvento, new { tipo, garcomId });
    public Task Aviso(Guid loja, string tipo, Guid id, Guid? mesaId, Guid? garcomId) => Enviar(loja, AvisoEvento, new { tipo, id, mesaId, garcomId });
    public Task SolicitacaoMudou(Guid loja, Guid id, string estado) => Enviar(loja, SolicitacaoEvento, new { id, estado });

    private async Task Enviar(Guid loja, string evento, object payload)
    {
        try
        {
            await hub.Clients.Group(DeliveryRealtimeEvents.EstablishmentGroup(loja)).SendAsync(evento, payload);
        }
        catch (Exception ex)
        {
            // Já está gravado; quem perdeu o aviso relê pelo polling de 15 s.
            log.LogWarning(ex, "Falha ao avisar {Evento} na loja {Loja}", evento, loja);
        }
    }
}

/// <summary>
/// Ganchos da sessão para os prompts de pedido (P08) e dinheiro (P14), dentro da mesma transação e com as mesas já travadas.
/// Lançar <see cref="MesasException"/> recusa o comando inteiro. Registre com <c>AddScoped&lt;IMesasSessaoGancho, ...&gt;</c>.
/// </summary>
public interface IMesasSessaoGancho
{
    /// <summary>Antes de encerrar. P14: com saldo e sem <paramref name="forcar"/> → recusa; com saldo só dono/gerente. P08: entrega presumida.</summary>
    Task AntesDeEncerrar(NpgsqlConnection c, NpgsqlTransaction tx, MesasContexto ctx, Guid sessaoId, bool forcar, string? motivo, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>Ao juntar: a sessão <paramref name="de"/> foi encerrada dentro de <paramref name="para"/>; mova itens da coletiva <paramref name="coletivaDe"/> para <paramref name="coletivaPara"/>.</summary>
    Task AoJuntar(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, Guid de, Guid para, Guid coletivaDe, Guid coletivaPara, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>Ao reabrir: recuse se algo pago impede (P14).</summary>
    Task AoReabrir(NpgsqlConnection c, NpgsqlTransaction tx, MesasContexto ctx, Guid sessaoId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Alertas que outro prompt calcula (P08: <c>pedido_pronto</c>, <c>atraso_cozinha</c>, <c>aprovacao_pendente</c>; P14: <c>pagamento_parcial</c>).</summary>
public interface IMesasAlertaFonte
{
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> AlertasAsync(NpgsqlConnection c, Guid loja, IReadOnlyCollection<Guid> sessaoIds,
        JsonObject config, CancellationToken ct);
}

/// <summary>Apaga respostas guardadas de <c>mesa_comando</c> com mais de 48 h (CONTRATO-API §0.2). Roda de hora em hora.</summary>
public sealed class MesaComandoLimpezaWorker(NpgsqlDataSource source, ILogger<MesaComandoLimpezaWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var c = await source.OpenConnectionAsync(stoppingToken);
                var n = await c.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM mesa_comando WHERE quando < NOW() - INTERVAL '48 hours'", cancellationToken: stoppingToken));
                if (n > 0) log.LogInformation("Mesas: {N} comandos antigos limpos", n);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable) { /* migração ainda não aplicada */ }
            catch (Exception ex) { log.LogWarning(ex, "Mesas: falha ao limpar mesa_comando"); }
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
