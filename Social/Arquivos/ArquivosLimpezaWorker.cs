using APIBack.Social.Nucleo;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Social.Arquivos;

/// <summary>
/// Apaga do R2 (e depois do banco) os arquivos do Social com <c>apagar_em</c> vencido: os que o dono apagou e os envios que nunca
/// foram confirmados. Roda a cada hora. Sem R2 configurado, não faz nada.
/// </summary>
public sealed class ArquivosLimpezaWorker(IServiceScopeFactory scopes, IArmazenamento armazenamento, ILogger<ArquivosLimpezaWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromHours(1);
    private const int Lote = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Espera o boot (migrações) antes da primeira rodada.
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            if (armazenamento.Configurado)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var apagados = await Rodada(scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>(), armazenamento, logger, stoppingToken);
                    if (apagados > 0) logger.LogInformation("Social: {Quantos} arquivo(s) apagado(s) do R2.", apagados);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Falha na limpeza dos arquivos do Social.");
                }
            }
            try { await Task.Delay(Intervalo, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Uma rodada. Um objeto que falha no R2 fica para a próxima; os outros seguem.</summary>
    public static async Task<int> Rodada(NpgsqlDataSource fonte, IArmazenamento armazenamento, ILogger logger, CancellationToken ct)
    {
        var vencidos = await SocialBanco.ComoSistema(fonte, ct, (c, tx) => c.QueryAsync<(Guid Id, string Key)>(new CommandDefinition("""
            SELECT id, r2_key FROM social_arquivos WHERE apagar_em <= NOW() ORDER BY apagar_em LIMIT @Lote
            """, new { Lote }, tx, cancellationToken: ct)));
        var apagados = 0;
        foreach (var (id, key) in vencidos)
        {
            try
            {
                await armazenamento.ApagarAsync(key, ct);
                await SocialBanco.ComoSistema(fonte, ct, (c, tx) => c.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM social_arquivos WHERE id = @id AND apagar_em <= NOW()", new { id }, tx, cancellationToken: ct)));
                apagados++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Social: não deu para apagar o arquivo {Id} do R2; tenta na próxima rodada.", id);
            }
        }
        return apagados;
    }
}
