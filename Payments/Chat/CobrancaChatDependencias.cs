using System;
using System.Threading.RateLimiting;
using APIBack.Extensions;
using APIBack.Payments.MercadoPago;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace APIBack.Payments.Chat
{
    /// <summary>Cobranca Pix do atendente (sessao 5). Program.cs chama AddCobrancaChat depois de AddPagamentosMercadoPago.</summary>
    public static class CobrancaChatDependencias
    {
        /// <summary>Criar/enviar/reemitir: 30 por minuto por usuario (protege a Meta e o MP de cliques em rajada).</summary>
        public const string PoliticaEscrita = "cobranca-chat";

        public static IServiceCollection AddCobrancaChat(this IServiceCollection services)
        {
            // Depois do ChatCobrancaEfeito do nucleo (mesma origem, ordem de registro): o status da cobranca ja foi atualizado.
            services.AddScoped<IPagamentoEfeitoHandler, ChatCobrancaPedidoEfeito>();
            services.AddScoped<ICobrancaChatMensageiro, CobrancaChatMensageiro>();
            services.AddScoped<ICobrancaPedidoAvancador, CobrancaPedidoAvancador>();
            services.AddScoped<CobrancaChatService>();
            services.AddScoped<CobrancaChatManutencao>();
            services.AddHostedService<CobrancaChatWorker>();
            services.Configure<RateLimiterOptions>(o => o.AddPolicy(PoliticaEscrita, ctx =>
                RateLimitPartition.GetFixedWindowLimiter(ctx.GetUserId()?.ToString() ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
            return services;
        }
    }
}
