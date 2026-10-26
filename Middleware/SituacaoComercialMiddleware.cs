using System.Threading.Tasks;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Service;
using Microsoft.AspNetCore.Http;

namespace APIBack.Middleware
{
    /// <summary>
    /// Trava unica da loja bloqueada ou ainda em implantacao. Roda depois da autenticacao: com estabelecimento no contexto e fora
    /// das rotas liberadas (SituacaoComercialRules.PermitidoQuandoBloqueado / OperacaoExigeInicio), uma loja "bloqueado" recebe 403
    /// ESTABELECIMENTO_BLOQUEADO em qualquer chamada, inclusive a negociacao do SignalR (/hubs). A situacao vem do banco
    /// (cache de 30 s), nunca do token, para o bloqueio valer mesmo com um token antigo.
    /// Super admin NAO tem excecao: ele recebe as mesmas travas em rota operacional (a Gestao, o login e a situacao comercial
    /// continuam abertos a qualquer usuario, ver SituacaoComercialRules.RotaSempreLiberada).
    /// </summary>
    public sealed class SituacaoComercialMiddleware
    {
        private readonly RequestDelegate _next;

        public SituacaoComercialMiddleware(RequestDelegate next) => _next = next;

        public async Task InvokeAsync(HttpContext context, ISituacaoComercialService situacoes, IProntidaoService? prontidao = null)
        {
            var estabelecimentoId = context.GetEstabelecimentoId();
            if (estabelecimentoId is null)
            {
                await _next(context);
                return;
            }

            var situacao = await situacoes.ObterAsync(estabelecimentoId.Value, context.RequestAborted);
            if (situacao?.Situacao == SituacoesComerciais.Bloqueado &&
                !SituacaoComercialRules.PermitidoQuandoBloqueado(context.Request.Path.Value, context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(ErrosOperacionais.Corpo(
                    ErrosOperacionais.EstabelecimentoBloqueado,
                    dados: string.IsNullOrWhiteSpace(situacao.BloqueadoMotivo) ? null
                        : new System.Collections.Generic.Dictionary<string, object?> { ["motivo"] = situacao.BloqueadoMotivo }));
                return;
            }
            // Falta de pagamento (ou assinatura encerrada): so as operacoes novas param. Ler, pagar, suporte e entregas em rota seguem.
            if (situacao?.Financeiro is "suspensa" or "encerrada" &&
                APIBack.Payments.Assinaturas.AcessoFinanceiroRules.Bloqueia(context.Request.Path.Value, context.Request.Method,
                    SituacaoComercialRules.PermitidoQuandoBloqueado(context.Request.Path.Value, context.Request.Method)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(ApiResponse<object>.Fail(
                    situacao.Financeiro == "encerrada"
                        ? "A assinatura desta loja terminou. Contrate um plano para voltar a criar pedidos."
                        : "Operacoes novas suspensas por falta de pagamento. Pague a fatura em aberto e o acesso volta sozinho.",
                    APIBack.Payments.Assinaturas.AcessoFinanceiroRules.CodigoSuspensa,
                    new System.Collections.Generic.Dictionary<string, object?> { ["financeiro"] = situacao.Financeiro, ["comoResolver"] = "/apps/configuracoes/plano" }));
                return;
            }
            if (situacao?.Situacao == SituacoesComerciais.Implantacao &&
                SituacaoComercialRules.OperacaoExigeInicio(context.Request.Path.Value, context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                // Mensagem curta e as 3 principais pendencias com o caminho de correcao (o front ja trata o 403 e o code).
                var pendencias = prontidao == null ? null : await TresPrincipaisAsync(prontidao, estabelecimentoId.Value, context);
                await context.Response.WriteAsJsonAsync(ErrosOperacionais.Corpo(ErrosOperacionais.OperacaoNaoIniciada,
                    "A operacao desta loja ainda nao foi iniciada.", pendencias));
                return;
            }

            await _next(context);
        }

        private static async Task<System.Collections.Generic.IReadOnlyList<PendenciaErroDto>?> TresPrincipaisAsync(
            IProntidaoService prontidao, System.Guid estabelecimentoId, HttpContext context)
        {
            try
            {
                var dto = await prontidao.ObterAsync(estabelecimentoId, visaoGestao: false, context.RequestAborted);
                return dto == null ? null : ProntidaoRegras.Pendencias(dto, max: 3);
            }
            catch (System.Exception)
            {
                // A explicacao e um complemento: sem ela, a recusa continua valendo.
                return null;
            }
        }
    }
}
