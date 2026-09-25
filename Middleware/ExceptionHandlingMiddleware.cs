using System;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.DTOs.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Middleware
{
    /// <summary>
    /// Ultima rede de seguranca: excecao nao tratada vira resposta JSON no formato ApiResponse.
    /// Sem isto o Kestrel devolvia um 500 vazio e SEM os cabecalhos de CORS, e o navegador mostrava
    /// "blocked by CORS policy" em vez do erro real. Fica depois do UseCors; a causa vai para o log
    /// com o traceId que tambem segue na resposta, para achar a linha certa.
    /// </summary>
    public sealed class ExceptionHandlingMiddleware
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly RequestDelegate _next;
        private readonly ILogger<ExceptionHandlingMiddleware> _logger;

        public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // Cliente desistiu da requisicao: nao e erro do servidor.
            }
            catch (Exception ex)
            {
                if (context.Response.HasStarted)
                {
                    _logger.LogError(ex, "Erro apos o inicio da resposta em {Method} {Path}.", context.Request.Method, context.Request.Path);
                    throw;
                }

                var traceId = context.TraceIdentifier;
                var (status, code, message) = Classify(ex);
                _logger.LogError(ex, "Erro nao tratado em {Method} {Path} (traceId {TraceId}, {Code}).",
                    context.Request.Method, context.Request.Path, traceId, code);

                context.Response.Clear();
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json; charset=utf-8";
                var body = ApiResponse<object>.Fail(message, code, new { traceId });
                await context.Response.WriteAsync(JsonSerializer.Serialize(body, Json));
            }
        }

        private static (int Status, string Code, string Message) Classify(Exception ex)
        {
            // Coluna/tabela que nao existe: o banco esta sem alguma migracao do delivery.
            if (ex is PostgresException { SqlState: "42703" or "42P01" })
            {
                return (503, "MIGRATION_PENDING",
                    "O banco de dados ainda nao recebeu todas as migracoes do delivery. Aplique-as e tente de novo.");
            }

            return (500, "INTERNAL_ERROR", "Erro interno. Informe o codigo de rastreio ao suporte.");
        }
    }
}
