using APIBack.Payments.MercadoPago;
using Microsoft.Extensions.DependencyInjection;

namespace APIBack.Payments.Assinaturas
{
    /// <summary>Assinaturas e compras da Zippy (Program.cs chama AddAssinaturasZippy depois de AddPagamentosMercadoPago).</summary>
    public static class AssinaturasRegistro
    {
        public static IServiceCollection AddAssinaturasZippy(this IServiceCollection services)
        {
            services.AddScoped<IMercadoPagoPreapprovalClient, MercadoPagoPreapprovalClient>();
            services.AddScoped<IAssinaturaService, AssinaturaService>();
            services.AddScoped<IPagamentoEfeitoHandler, AssinaturaPagamentoEfeito>();
            services.AddScoped<IPagamentoEfeitoHandler, CompraExtraPagamentoEfeito>();
            services.AddScoped<AssinaturaWebhookHandler>();
            services.AddScoped<IMercadoPagoTopicoHandler>(sp => sp.GetRequiredService<AssinaturaWebhookHandler>());
            services.AddHostedService<AssinaturaWorker>();
            return services;
        }
    }
}
