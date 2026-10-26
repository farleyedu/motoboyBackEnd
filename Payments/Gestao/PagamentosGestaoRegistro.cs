using APIBack.Payments.Conexao;
using APIBack.Payments.MercadoPago;
using Microsoft.Extensions.DependencyInjection;

namespace APIBack.Payments.Gestao
{
    /// <summary>Gestao de Pagamentos + conexao OAuth das lojas (sessao 2). Chamado por AddPagamentosMercadoPago.</summary>
    public static class PagamentosGestaoRegistro
    {
        public static IServiceCollection AddPagamentosGestaoEConexao(this IServiceCollection services)
        {
            services.AddScoped<IMercadoPagoConexaoLojaService, MercadoPagoConexaoLojaService>();
            services.AddScoped<IPagamentosGestaoService, PagamentosGestaoService>();
            services.AddScoped<IMercadoPagoTopicoHandler, MpConnectTopicoHandler>();
            services.AddHostedService<MercadoPagoContasLojaWorker>();
            return services;
        }
    }
}
