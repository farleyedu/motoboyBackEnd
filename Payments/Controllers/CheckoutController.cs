using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Payments.Dtos.Checkout;
using APIBack.Payments.Services.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Payments.Controllers
{
    [ApiController]
    [Route("api/checkout")]
    public class CheckoutController : ControllerBase
    {
        private readonly ICheckoutPaymentService _checkoutPaymentService;
        private readonly ILogger<CheckoutController> _logger;

        public CheckoutController(
            ICheckoutPaymentService checkoutPaymentService,
            ILogger<CheckoutController> logger)
        {
            _checkoutPaymentService = checkoutPaymentService;
            _logger = logger;
        }

        [HttpPost("pix")]
        [ProducesResponseType(typeof(ApiResponse<CheckoutPaymentResponse>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> CreatePix([FromBody] CreatePixCheckoutRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<object>.Fail("Requisicao invalida."));
            }

            var userId = HttpContext.GetUserId();
            if (!userId.HasValue || userId.Value <= 0)
            {
                return Unauthorized(ApiResponse<object>.Fail("Usuario nao autenticado."));
            }

            try
            {
                var response = await _checkoutPaymentService.CreatePixAsync(userId.Value, HttpContext.GetEstabelecimentoId(), request);
                return Ok(ApiResponse<CheckoutPaymentResponse>.Ok(response));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao criar checkout PIX para UserId={UserId}", userId.Value);
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao criar checkout PIX."));
            }
        }

        [HttpPost("cartao")]
        [ProducesResponseType(typeof(ApiResponse<CheckoutPaymentResponse>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> CreateCard([FromBody] CreateCardCheckoutRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<object>.Fail("Requisicao invalida."));
            }

            var userId = HttpContext.GetUserId();
            if (!userId.HasValue || userId.Value <= 0)
            {
                return Unauthorized(ApiResponse<object>.Fail("Usuario nao autenticado."));
            }

            try
            {
                var response = await _checkoutPaymentService.CreateCardAsync(userId.Value, HttpContext.GetEstabelecimentoId(), request);
                return Ok(ApiResponse<CheckoutPaymentResponse>.Ok(response));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao criar checkout cartao para UserId={UserId}", userId.Value);
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao criar checkout cartao."));
            }
        }

        [HttpGet("{paymentId:long}")]
        [ProducesResponseType(typeof(ApiResponse<CheckoutPaymentStatusResponse>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetStatus([FromRoute] long paymentId)
        {
            var userId = HttpContext.GetUserId();
            if (!userId.HasValue || userId.Value <= 0)
            {
                return Unauthorized(ApiResponse<object>.Fail("Usuario nao autenticado."));
            }

            try
            {
                var response = await _checkoutPaymentService.GetStatusAsync(paymentId, userId.Value, HttpContext.IsSuperAdmin());
                if (response == null)
                {
                    return NotFound(ApiResponse<object>.Fail("Pagamento nao encontrado."));
                }

                return Ok(ApiResponse<CheckoutPaymentStatusResponse>.Ok(response));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao consultar checkout paymentId={PaymentId}", paymentId);
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao consultar checkout."));
            }
        }
    }
}
