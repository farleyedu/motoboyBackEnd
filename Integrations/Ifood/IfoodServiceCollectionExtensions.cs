using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace APIBack.Integrations.Ifood;

public static class IfoodServiceCollectionExtensions
{
    public static IServiceCollection AddIfoodIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<IfoodOptions>, IfoodOptionsValidator>();
        services.AddOptions<IfoodOptions>().Bind(configuration.GetSection(IfoodOptions.SectionName)).ValidateOnStart();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddHttpClient(IfoodAuthenticationClient.ClientName, (provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<IfoodOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(options.Enabled ? options.HttpTimeoutSeconds : 15);
            client.MaxResponseContentBufferSize = 32768;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .RedactLoggedHeaders(_ => true).RemoveAllLoggers();
        services.AddSingleton<IIfoodAuthenticationClient, IfoodAuthenticationClient>();
        services.AddSingleton<IIfoodTokenProvider, IfoodTokenProvider>();
        services.AddSingleton<IfoodSecretProtector>();
        services.AddSingleton<IIfoodConnectionRepository, IfoodConnectionRepository>();
        services.AddSingleton<IfoodMerchantAccess>();
        services.AddSingleton<Config.IIfoodRuntimeSettingsRepository, Config.IfoodRuntimeSettingsRepository>();
        services.AddHttpClient(Events.IfoodEventsClient.ClientName, (provider, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<IfoodOptions>>().Value.HttpTimeoutSeconds);
            client.MaxResponseContentBufferSize = 4 * 1024 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .RedactLoggedHeaders(_ => true).RemoveAllLoggers();
        services.AddSingleton<Events.IIfoodEventsClient, Events.IfoodEventsClient>();
        services.AddSingleton<Events.IfoodInboxRepository>();
        services.AddSingleton<Events.IIfoodInboxRepository>(provider => provider.GetRequiredService<Events.IfoodInboxRepository>());
        services.AddSingleton<Events.IfoodPollingService>();
        services.AddHostedService<Events.IfoodPollingWorker>();
        services.AddHttpClient(Orders.IfoodOrderDetailsClient.ClientName, (provider, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<IfoodOptions>>().Value.HttpTimeoutSeconds);
            client.MaxResponseContentBufferSize = 4 * 1024 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .RedactLoggedHeaders(_ => true).RemoveAllLoggers();
        services.AddSingleton<Orders.IIfoodOrderDetailsClient, Orders.IfoodOrderDetailsClient>();
        services.AddSingleton<Orders.IfoodOrderImportRepository>();
        services.AddSingleton<Orders.IfoodOrderImportService>();
        services.AddHostedService<Orders.IfoodOrderImportWorker>();
        services.AddSingleton<Orders.IfoodOrderProjectionRepository>();
        services.AddSingleton<Orders.IfoodOrderProjectionService>();
        services.AddHostedService<Orders.IfoodOrderProjectionWorker>();
        services.AddHttpClient(Commands.IfoodCommandClient.ClientName,(provider,client)=>
        {
            client.Timeout=TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<IfoodOptions>>().Value.HttpTimeoutSeconds);
            client.MaxResponseContentBufferSize=32768;
        }).ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler { AllowAutoRedirect=false,UseCookies=false })
            .RedactLoggedHeaders(_=>true).RemoveAllLoggers();
        services.AddSingleton<Commands.IIfoodCommandClient,Commands.IfoodCommandClient>();
        services.AddSingleton<Commands.IfoodCommandRepository>();
        services.AddSingleton<Commands.IfoodCommandService>();
        services.AddHostedService<Commands.IfoodCommandWorker>();
        services.AddHttpClient(Exceptions.IfoodCancellationReasonsClient.ClientName,(provider,client)=>
        {
            client.Timeout=TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<IfoodOptions>>().Value.HttpTimeoutSeconds);
            client.MaxResponseContentBufferSize=256*1024;
        }).ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler { AllowAutoRedirect=false,UseCookies=false })
            .RedactLoggedHeaders(_=>true).RemoveAllLoggers();
        services.AddSingleton<Exceptions.IIfoodCancellationReasonsClient,Exceptions.IfoodCancellationReasonsClient>();
        services.AddSingleton<Exceptions.IfoodExceptionService>();
        services.AddHostedService<Exceptions.IfoodExceptionWorker>();
        services.AddHttpClient(Completion.IfoodDeliveryCodeClient.ClientName, (provider, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptionsMonitor<IfoodOptions>>().CurrentValue.HttpTimeoutSeconds);
            client.MaxResponseContentBufferSize = 32768;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .RedactLoggedHeaders(_ => true).RemoveAllLoggers();
        services.AddSingleton<Completion.IIfoodDeliveryCodeClient, Completion.IfoodDeliveryCodeClient>();
        services.AddSingleton<Completion.IfoodCompletionService>();
        services.AddHostedService<Completion.IfoodCompletionWorker>();
        services.AddSingleton<Reconciliation.IIfoodReconciliationRepository, Reconciliation.IfoodReconciliationRepository>();
        services.AddSingleton<Retention.IfoodRetentionService>();
        services.AddHostedService<Retention.IfoodRetentionWorker>();
        return services;
    }
}
