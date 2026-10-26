using System;
using System.Net.Http.Headers;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace APIBack.Payments.MercadoPago
{
    /// <summary>Tudo do nucleo de pagamentos Mercado Pago num lugar so (Program.cs chama AddPagamentosMercadoPago).</summary>
    public static class PagamentosRegistro
    {
        /// <summary>Limite por IP das rotas publicas de pagamento (cardapio e pagina Pix).</summary>
        public const string PoliticaPublica = "public-payment";

        public static IServiceCollection AddPagamentosMercadoPago(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<MercadoPagoOptions>(configuration.GetSection(MercadoPagoOptions.Secao));
            services.AddHttpClient(MercadoPagoClient.NomeHttpClient, (sp, client) =>
            {
                var o = sp.GetRequiredService<IOptions<MercadoPagoOptions>>().Value;
                client.BaseAddress = new Uri(string.IsNullOrWhiteSpace(o.BaseUrl) ? "https://api.mercadopago.com/" : o.BaseUrl);
                client.Timeout = TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSegundos, 5, 60));
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ZippyPagamentos/1.0");
            });

            services.AddSingleton<IPagamentoSegredoProtector, PagamentoSegredoProtector>();
            services.AddScoped<IMercadoPagoClient, MercadoPagoClient>();
            services.AddScoped<IMercadoPagoCredencialResolver, MercadoPagoCredencialResolver>();
            services.AddScoped<IContaLojaTokenRenovador, ContaLojaTokenRenovador>();
            services.AddScoped<IPagamentoEfeitoHandler, CardapioPagamentoEfeito>();
            services.AddScoped<IPagamentoEfeitoHandler, ChatCobrancaEfeito>();
            services.AddScoped<IPagamentoSincronizador, PagamentoSincronizador>();
            services.AddScoped<PagamentoCriacaoBase>();
            services.AddScoped<IPixChargeService, PixChargeService>();
            services.AddScoped<ICardPaymentService, CardPaymentService>();
            services.AddScoped<IPagamentoEstornoService, PagamentoEstornoService>();
            services.AddScoped<IMercadoPagoWebhookService, MercadoPagoWebhookService>();
            services.AddHostedService<PagamentoConciliadorWorker>();
            services.AddHostedService<PagamentoOutboxPublisher>();
            APIBack.Payments.Gestao.PagamentosGestaoRegistro.AddPagamentosGestaoEConexao(services);

            services.Configure<RateLimiterOptions>(o => o.AddPolicy(PoliticaPublica, ctx =>
                RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
            return services;
        }
    }
}
