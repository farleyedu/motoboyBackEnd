// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Security.Cryptography;
using System.Text;
using APIBack.Automation.Infra;
using APIBack.Automation.Interfaces;
using Microsoft.Extensions.Options;

namespace APIBack.Automation.Infra
{
    public class WebhookSignatureValidator : IWebhookSignatureValidator
    {
        private readonly AutomationOptions _opcoes;

        public WebhookSignatureValidator(IOptions<AutomationOptions> options)
        {
            _opcoes = options.Value;
        }

        public bool ValidarXHubSignature256(string? cabecalhoAssinatura, string corpoRequisicao)
        {
            if (!_opcoes.StrictSignatureValidation)
            {
                return true; // validation disabled by config
            }

            if (string.IsNullOrWhiteSpace(cabecalhoAssinatura)) return false;

            // TODO: calcular HMAC SHA256 (x-hub-signature-256) e comparar de forma segura
            try
            {
                var segredo = _opcoes.Meta?.AppSecret ?? string.Empty;
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(segredo));
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(corpoRequisicao));
                var hashString = "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
                return string.Equals(hashString, cabecalhoAssinatura, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
