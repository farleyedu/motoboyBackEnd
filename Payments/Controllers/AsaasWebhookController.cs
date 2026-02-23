using APIBack.DTOs.Common;
using APIBack.Payments.Dtos.Webhook;
using APIBack.Payments.Services.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Payments.Controllers
{
    [ApiController]
    [AllowAnonymous]
    [Route("api/webhooks/asaas")]
    public class AsaasWebhookController : ControllerBase
    {
        private const string AccessTokenHeader = "asaas-access-token";

        private readonly ICheckoutWebhookService _checkoutWebhookService;

        public AsaasWebhookController(ICheckoutWebhookService checkoutWebhookService)
        {
            _checkoutWebhookService = checkoutWebhookService;
        }

        [HttpPost]
        [ProducesResponseType(typeof(ApiResponse<WebhookProcessResponse>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> Post([FromBody] AsaasWebhookRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<object>.Fail("Payload de webhook invalido."));
            }

            var token = Request.Headers[AccessTokenHeader].ToString();
            var result = await _checkoutWebhookService.ProcessAsaasAsync(request, token);

            if (!result.Success && string.Equals(result.ErrorCode, "UNAUTHORIZED", StringComparison.Ordinal))
            {
                return Unauthorized(ApiResponse<object>.Fail(result.Message));
            }

            // Mantemos 200 para evitar reenvio automatico do Asaas em erros de processamento.
            return Ok(ApiResponse<WebhookProcessResponse>.Ok(result));
        }
    }
}
