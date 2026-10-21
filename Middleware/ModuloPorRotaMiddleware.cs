using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Extensions;
using APIBack.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace APIBack.Middleware
{
    /// <summary>
    /// Faz o modulo comercial valer no servidor para as familias de rota sem <c>[RequireModule]</c> (ver <see cref="ModulosPorRota"/>).
    /// Roda depois da autenticacao e da situacao comercial. Super admin, chamadas sem loja no contexto e loja em teste (tudo liberado)
    /// passam. Configuracao <c>ModuleEnforcement</c>: "warn" (padrao) so registra o que seria bloqueado, para ligar "enforce"
    /// sem surpresa com lojas de modulos inconsistentes; "off" desliga.
    /// </summary>
    public sealed class ModuloPorRotaMiddleware
    {
        private static readonly ConcurrentDictionary<(Guid Store, string Module), DateTime> LastWarning = new();
        private readonly RequestDelegate _next;

        public ModuloPorRotaMiddleware(RequestDelegate next) => _next = next;

        public async Task InvokeAsync(HttpContext context, ISituacaoComercialService situacoes, IConfiguration configuration, ILogger<ModuloPorRotaMiddleware> logger)
        {
            var modo = ModulosPorRota.Modo(configuration["ModuleEnforcement"]);
            var modulo = modo == ModulosPorRota.Off ? null : ModulosPorRota.Exigido(context.Request.Path.Value);
            if (modulo is null || context.GetEstabelecimentoId() is not Guid loja || context.IsSuperAdmin() ||
                context.GetEstabelecimentoModulosAtivos().Any(m => string.Equals(m, modulo, StringComparison.OrdinalIgnoreCase)))
            {
                await _next(context);
                return;
            }

            var situacao = await situacoes.ObterAsync(loja, context.RequestAborted);
            if (situacao?.Situacao == SituacoesComerciais.Teste)
            {
                await _next(context);
                return;
            }

            if (modo == ModulosPorRota.Enforce)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    error = "Este recurso nao faz parte do plano da loja.",
                    code = ModulosPorRota.CodigoModuloInativo,
                    modulo
                });
                return;
            }

            // Uma linha por loja e modulo a cada 10 minutos: o bastante para achar falsos positivos sem inundar o log.
            var now = DateTime.UtcNow;
            if (LastWarning.AddOrUpdate((loja, modulo), now, (_, previous) => now - previous > TimeSpan.FromMinutes(10) ? now : previous) == now)
                logger.LogWarning("Modulo {Modulo} inativo na loja {Loja}, mas a rota {Method} {Path} seria bloqueada com ModuleEnforcement=enforce.",
                    modulo, loja, context.Request.Method, context.Request.Path.Value);
            await _next(context);
        }
    }
}
