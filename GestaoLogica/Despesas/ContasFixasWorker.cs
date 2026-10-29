using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.GestaoLogica.Despesas;

/// <summary>
/// Gera sozinho as contas fixas de valor fixo do mês em toda loja que tem alguma (CONTRATOS §9.8, <c>POST contas-fixas/gerar</c>).
/// Roda a cada 6 horas: gerar é idempotente (a conta do mês nunca sai duas vezes), então repetir não duplica. O responsável do
/// lançamento é quem cadastrou a conta fixa mais antiga da loja. A conta de valor variável continua esperando o valor.
/// </summary>
public sealed class ContasFixasWorker(IServiceScopeFactory scopes, ILogger<ContasFixasWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Espera o boot (migrações) antes da primeira rodada.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await GerarTodas(scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>(),
                    scope.ServiceProvider.GetRequiredService<IRecorrenciasService>(), DateOnly.FromDateTime(DateTime.UtcNow), logger, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falha ao gerar as contas fixas do mês.");
            }
            try { await Task.Delay(Intervalo, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Uma rodada: gera o mês de <paramref name="dia"/> em cada loja. Uma loja com erro não impede as outras.</summary>
    public static async Task<int> GerarTodas(NpgsqlDataSource fonte, IRecorrenciasService recorrencias, DateOnly dia, ILogger logger,
        CancellationToken ct)
    {
        List<(Guid Loja, int Responsavel)> lojas;
        await using (var c = await fonte.OpenConnectionAsync(ct))
        {
            // Sem a tabela (migração ainda não aplicada neste ambiente), não há o que gerar.
            if (!await c.ExecuteScalarAsync<bool>("SELECT to_regclass('gestao_contas_fixas') IS NOT NULL")) return 0;
            lojas = (await c.QueryAsync<(Guid, int)>(@"
SELECT estabelecimento_id, (array_agg(criado_por ORDER BY criado_em, id))[1]
  FROM gestao_contas_fixas WHERE NOT arquivada AND NOT valor_varia GROUP BY estabelecimento_id")).ToList();
        }
        var geradas = 0;
        foreach (var (loja, responsavel) in lojas)
        {
            try { geradas += (await recorrencias.Gerar(loja, responsavel, new DateOnly(dia.Year, dia.Month, 1), ct)).Geradas.Count; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Contas fixas da loja {Loja} não geradas.", loja);
            }
        }
        if (geradas > 0) logger.LogInformation("Contas fixas geradas: {Geradas} em {Lojas} lojas.", geradas, lojas.Count);
        return geradas;
    }
}
