using APIBack.DTOs.Common;
using APIBack.Payments.Dtos.Checkout;
using APIBack.Payments.Dtos.CheckoutPublic;
using APIBack.Payments.Services.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Payments.Controllers
{
    [ApiController]
    [Route("api/checkout/public")]
    [AllowAnonymous]
    public class PublicCheckoutController : ControllerBase
    {
        private readonly IPublicCheckoutService _publicCheckoutService;
        private readonly ILogger<PublicCheckoutController> _logger;

        public PublicCheckoutController(
            IPublicCheckoutService publicCheckoutService,
            ILogger<PublicCheckoutController> logger)
        {
            _publicCheckoutService = publicCheckoutService;
            _logger = logger;
        }

        [HttpPost("pix")]
        [ProducesResponseType(typeof(ApiResponse<CheckoutPaymentResponse>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> CreatePix([FromBody] CreatePublicPixCheckoutRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<object>.Fail("Requisicao invalida."));
            }

            try
            {
                var response = await _publicCheckoutService.CreatePixAsync(request);
                return Ok(ApiResponse<CheckoutPaymentResponse>.Ok(response));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao criar checkout publico PIX");
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao criar checkout PIX."));
            }
        }

        [HttpPost("cartao")]
        [ProducesResponseType(typeof(ApiResponse<CheckoutPaymentResponse>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> CreateCard([FromBody] CreatePublicCardCheckoutRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<object>.Fail("Requisicao invalida."));
            }

            try
            {
                var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();
                var response = await _publicCheckoutService.CreateCardAsync(request, remoteIp);
                return Ok(ApiResponse<CheckoutPaymentResponse>.Ok(response));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao criar checkout publico cartao");
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao criar checkout cartao."));
            }
        }
    }
}
