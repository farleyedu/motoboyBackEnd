using System.Threading.Tasks;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Service;
using Microsoft.AspNetCore.Http;

namespace APIBack.Middleware
{
    /// <summary>
    /// Trava unica da loja bloqueada. Roda depois da autenticacao: com estabelecimento no contexto, fora do super admin
    /// e fora das rotas liberadas (SituacaoComercialRules.PermitidoQuandoBloqueado), uma loja "bloqueado" recebe 403
    /// ESTABELECIMENTO_BLOQUEADO em qualquer chamada, inclusive a negociacao do SignalR (/hubs). A situacao vem do banco
    /// (cache de 30 s), nunca do token, para o bloqueio valer mesmo com um token antigo.
    /// </summary>
    public sealed class SituacaoComercialMiddleware
    {
        private readonly RequestDelegate _next;

        public SituacaoComercialMiddleware(RequestDelegate next) => _next = next;

        public async Task InvokeAsync(HttpContext context, ISituacaoComercialService situacoes)
        {
            var estabelecimentoId = context.GetEstabelecimentoId();
            if (estabelecimentoId is null || context.IsSuperAdmin())
            {
                await _next(context);
                return;
            }

            var situacao = await situacoes.ObterAsync(estabelecimentoId.Value, context.RequestAborted);
            if (situacao?.Situacao == SituacoesComerciais.Bloqueado &&
                !SituacaoComercialRules.PermitidoQuandoBloqueado(context.Request.Path.Value, context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(ApiResponse<object>.Fail(
                    "Acesso suspenso. Fale com a Zippy para reativar.", SituacaoComercialRules.CodigoBloqueado));
                return;
            }
            if (situacao?.Situacao == SituacoesComerciais.Implantacao &&
                SituacaoComercialRules.OperacaoExigeInicio(context.Request.Path.Value, context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(ApiResponse<object>.Fail(
                    "A operacao desta loja ainda nao foi iniciada.", SituacaoComercialRules.CodigoOperacaoNaoIniciada));
                return;
            }

            await _next(context);
        }
    }
}
