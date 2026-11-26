using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Turnos;

/// <summary>
/// Fechar pelo horário não gera evento. A cada minuto os turnos das lojas são avaliados (o que também faz uma mudança de
/// horário valer só daqui para a frente) e quem ficou sem crédito sai do ar. Sem este serviço o cálculo continua certo:
/// toda consulta reconstrói o intervalo desde a última avaliação.
/// </summary>
public sealed class TurnoEntregaWorker : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(1);
    private readonly IServiceScopeFactory _escopos;
    private readonly ILogger<TurnoEntregaWorker> _logger;

    public TurnoEntregaWorker(IServiceScopeFactory escopos, ILogger<TurnoEntregaWorker> logger)
    {
        _escopos = escopos;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Intervalo);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await RodarAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Falha ao avaliar os turnos de entrega.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task RodarAsync(CancellationToken ct)
    {
        using var escopo = _escopos.CreateScope();
        var dataSource = escopo.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        Guid[] lojas;
        await using (var connection = await dataSource.OpenConnectionAsync(ct))
        {
            if (!await TurnoEntregaStore.SchemaProntoAsync(connection, null, ct)) return;
            lojas = (await connection.QueryAsync<Guid>(new CommandDefinition(@"
SELECT e.id FROM estabelecimentos e
 WHERE e.situacao_comercial IN ('teste', 'ativo')
   AND (EXISTS (SELECT 1 FROM delivery_turno_estado t WHERE t.estabelecimento_id = e.id)
     OR EXISTS (SELECT 1 FROM motoboy_estabelecimento me WHERE me.estabelecimento_id = e.id AND me.ativo = TRUE));",
                cancellationToken: ct))).ToArray();
        }
        foreach (var loja in lojas)
        {
            // Um escopo por loja: o repositório de sessões guarda estado da própria operação.
            using var porLoja = _escopos.CreateScope();
            await porLoja.ServiceProvider.GetRequiredService<ITurnoEntregaVarredura>().TirarSemCreditoAsync(loja, ct);
        }
    }
}
