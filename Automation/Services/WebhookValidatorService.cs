// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class WebhookValidatorService
    {
        private readonly IWebhookSignatureValidator _signatureValidator;
        private readonly ILogger<WebhookValidatorService> _logger;

        public WebhookValidatorService(IWebhookSignatureValidator signatureValidator, ILogger<WebhookValidatorService> logger)
        {
            _signatureValidator = signatureValidator;
            _logger = logger;
        }

        public async Task<string> ReadBodyAsync(HttpRequest request)
        {
            request.EnableBuffering();
            string payload;
            using (var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
            {
                payload = await reader.ReadToEndAsync();
                request.Body.Position = 0;
            }

            return payload;
        }

        public bool ValidateSignature(string? signature, string payload)
        {
            if (string.IsNullOrWhiteSpace(signature))
            {
                _logger.LogWarning("Assinatura X-Hub-Signature-256 ausente");
                return false;
            }

            var valido = _signatureValidator.ValidarXHubSignature256(signature, payload);
            if (!valido)
            {
                _logger.LogWarning("Invalid X-Hub-Signature-256");
            }

            return valido;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
