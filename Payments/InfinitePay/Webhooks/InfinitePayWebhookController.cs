using System.IO;
using System.Text;
using System.Threading.Tasks;
using APIBack.DTOs.Common;
using APIBack.Payments.MercadoPago;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace APIBack.Payments.InfinitePay.Webhooks
{
    /// <summary>
    /// Webhook da InfinitePay. NAO tem assinatura: o corpo e nao confiavel e nada vira "pago" aqui. 2xx so depois de gravar o aviso (ou
    /// reconhecer duplicata); 400 formato, 404 order_nsu que nao e nosso, 429 excesso por cobranca ou por IP.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [EnableRateLimiting(InfinitePayWebhookLimite.Politica)]
    [Route("api/webhooks/infinitepay")]
    public sealed class InfinitePayWebhookController : ControllerBase
    {
        private readonly IInfinitePayWebhookService _webhook;

        public InfinitePayWebhookController(IInfinitePayWebhookService webhook) => _webhook = webhook;

        [HttpPost]
        [RequestSizeLimit(InfinitePayWebhookRegras.CorpoMaximoBytes)]
        public async Task<IActionResult> Receber()
        {
            string corpo;
            using (var leitor = new StreamReader(Request.Body, Encoding.UTF8))
                corpo = await leitor.ReadToEndAsync(HttpContext.RequestAborted);

            var r = await _webhook.ReceberAsync(corpo, HttpContext.RequestAborted);
            return r.Desfecho switch
            {
                DesfechoRecebimento.Aceito => Ok(new { recebido = true }),
                DesfechoRecebimento.Duplicado => Ok(new { recebido = true, duplicado = true }),
                DesfechoRecebimento.SemCorrelacao => NotFound(ApiResponse<object>.Fail("Cobranca desconhecida.", PagamentoCodigos.NaoEncontrado)),
                DesfechoRecebimento.LimiteExcedido => StatusCode(StatusCodes.Status429TooManyRequests, ApiResponse<object>.Fail("Limite de avisos.", PagamentoCodigos.LimiteRequisicoes)),
                _ => BadRequest(ApiResponse<object>.Fail("Aviso invalido.", PagamentoCodigos.RequisicaoInvalida))
            };
        }
    }

    public static class InfinitePayWebhookLimite
    {
        public const string Politica = "infinitepay-webhook";
    }
}
