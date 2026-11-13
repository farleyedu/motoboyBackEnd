using System;
using System.Threading.Tasks;
using APIBack.Payments.Controllers;
using APIBack.Payments.MercadoPago;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

namespace APIBack.Payments.InfinitePay.Cardapio
{
    /// <summary>
    /// Link hospedado da InfinitePay para o pedido do cardapio. Mesmo segundo fator (id + codigo) e mesma politica das rotas Pix/cartao;
    /// o corpo nunca leva valor. Devolver ao cliente o link nao e prova de pagamento: o pedido so anda por /payment_check no servidor.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [EnableRateLimiting(PagamentosRegistro.PoliticaPublica)]
    [Route("api/public/cardapio/pedidos/{prePedidoId:guid}/pagamentos/checkout")]
    public sealed class CardapioCheckoutHospedadoController : PagamentoControllerBase
    {
        private readonly ILogger<CardapioCheckoutHospedadoController> _logger;

        public CardapioCheckoutHospedadoController(ILogger<CardapioCheckoutHospedadoController> logger) => _logger = logger;

        [HttpPost]
        public Task<IActionResult> Criar(Guid prePedidoId, [FromBody] PublicPixPedidoRequest? body, [FromServices] ICardapioPagamentoService cardapio) =>
            Executar(() => cardapio.CriarCheckoutHospedadoAsync(prePedidoId, body?.Codigo, HttpContext.RequestAborted), _logger);
    }
}
