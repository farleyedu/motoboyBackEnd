using System;
using System.Net.Http.Headers;
using System.Threading.RateLimiting;
using APIBack.Payments.InfinitePay.Checkout;
using APIBack.Payments.InfinitePay.Cliente;
using APIBack.Payments.InfinitePay.Webhooks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace APIBack.Payments.InfinitePay
{
    /// <summary>
    /// Registro do gateway InfinitePay. PENDENTE: chamar <c>services.AddPagamentosInfinitePay(configuration)</c> em Program.cs ou ao fim de
    /// PagamentosRegistro.AddPagamentosMercadoPago (arquivos compartilhados, fora do escopo do prompt 02).
    /// </summary>
    public static class InfinitePayRegistro
    {
        public static IServiceCollection AddPagamentosInfinitePay(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<InfinitePayOptions>(configuration.GetSection(InfinitePayOptions.Secao));
            services.AddHttpClient(InfinitePayClient.NomeHttpClient, (sp, client) =>
            {
                var o = sp.GetRequiredService<IOptions<InfinitePayOptions>>().Value;
                var baseUrl = string.IsNullOrWhiteSpace(o.BaseUrl) ? "https://api.checkout.infinitepay.io/" : o.BaseUrl;
                client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
                client.Timeout = TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSegundos, 5, 30));
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ZippyPagamentos/1.0");
            });
            services.AddScoped<IInfinitePayClient, InfinitePayClient>();
            services.AddScoped<IInfinitePayCheckoutService, InfinitePayCheckoutService>();
            services.AddScoped<APIBack.Payments.InfinitePay.Cardapio.ICardapioCheckoutHospedadoService, APIBack.Payments.InfinitePay.Cardapio.CardapioCheckoutHospedadoService>();

            // Webhook sem assinatura (prompt 04). O worker precisa de IPagamentoEfeitoHandler (registrados em PagamentosRegistro).
            services.AddScoped<IInfinitePayWebhookService, InfinitePayWebhookService>();
            services.AddScoped<IInfinitePayWebhookProcessor, InfinitePayWebhookProcessor>();
            services.AddHostedService<InfinitePayWebhookWorker>();
            services.Configure<RateLimiterOptions>(o => o.AddPolicy(InfinitePayWebhookLimite.Politica, ctx =>
                RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
            return services;
        }
    }
}
