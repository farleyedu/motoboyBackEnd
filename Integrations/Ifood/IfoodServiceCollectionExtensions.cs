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
        return services;
    }
}
