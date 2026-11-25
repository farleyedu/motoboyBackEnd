namespace APIBack.Assistente;

/// <summary>Apaga conversas paradas há mais de 30 dias (mensagens vão junto; ação e auditoria ficam). Roda a cada 6 horas.</summary>
public sealed class AssistenteLimpeza(IServiceScopeFactory scopes, ILogger<AssistenteLimpeza> logger, TimeProvider relogio) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IAssistenteRepositorio>();
                var apagadas = await repo.ApagarConversasAntigas(relogio.GetUtcNow() - AssistenteService.GuardaConversa, stoppingToken);
                if (apagadas > 0) logger.LogInformation("Assistente: {Quantas} conversas antigas apagadas", apagadas);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Tabela ainda não migrada ou banco fora: tenta de novo na próxima volta.
                logger.LogWarning(ex, "Assistente: limpeza de conversas falhou");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public static class AssistenteRegistro
{
    /// <summary>Catálogo (carregado e validado já no registro: catálogo inválido derruba o boot), ações, serviço e limpeza.</summary>
    public static IServiceCollection AddAssistente(this IServiceCollection services)
    {
        services.AddSingleton(CatalogoAssistente.CarregarEmbutido());
        services.AddScoped<IAcaoAssistente, AcaoPausarProduto>();
        services.AddScoped<IAcaoAssistente, AcaoVoltarProduto>();
        services.AddScoped<IAcaoAssistente, AcaoCriarHorarioEspecial>();
        services.AddScoped<IAcaoAssistente, AcaoRemoverHorarioEspecial>();
        services.AddScoped<IAcaoAssistente, AcaoAbrirLojaHoje>();
        services.AddScoped<IAssistenteRepositorio, AssistenteRepositorio>();
        services.AddScoped<IAssistenteService, AssistenteService>();
        services.AddScoped<IAssistenteConfigRepositorio, AssistenteConfigRepositorio>();
        services.AddSingleton<CifraChavesAssistente>();
        services.AddSingleton<ChavesIA>();
        services.AddSingleton<IChavesIARepositorio, ChavesIARepositorio>();
        services.AddSingleton<FabricaProvedores>();
        services.AddSingleton<IFabricaProvedores>(sp => sp.GetRequiredService<FabricaProvedores>());
        services.AddSingleton<ICriadorProvedores>(sp => sp.GetRequiredService<FabricaProvedores>());
        services.AddScoped(sp => new DiagnosticoIA(sp.GetRequiredService<ICriadorProvedores>(), sp.GetRequiredService<IEstadoCadeiaRepositorio>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<DiagnosticoIA>>(),
            (cfg, itemId, ct) => sp.GetRequiredService<IAssistenteService>().Testar(cfg, itemId, true, null, ct)));
        services.AddScoped<GestaoChavesIA>();
        services.AddScoped<IEstadoCadeiaRepositorio, EstadoCadeiaRepositorio>();
        services.AddScoped(sp => new CadeiaIA(sp.GetRequiredService<IFabricaProvedores>(), sp.GetRequiredService<IEstadoCadeiaRepositorio>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<CadeiaIA>>(), sp.GetRequiredService<ChavesIA>()));
        services.AddSingleton<CacheModelosIA>();
        services.AddScoped<IAssistenteUsoRepositorio, AssistenteUsoRepositorio>();
        services.AddHostedService<AssistenteLimpeza>();
        return services;
    }
}
