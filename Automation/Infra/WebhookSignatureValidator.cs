// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Security.Cryptography;
using System.Text;
using APIBack.Automation.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace APIBack.Automation.Infra
{
    /// <summary>
    /// Confere o X-Hub-Signature-256 (HMAC-SHA256 do corpo com o App Secret da Meta). A regra:
    /// - segredo configurado: a assinatura e SEMPRE exigida, com ou sem StrictSignatureValidation;
    /// - sem segredo e Strict ligado: tudo e recusado (configuracao incompleta);
    /// - sem segredo e Strict desligado: aceita, mas grita no log, porque qualquer um poderia forjar mensagens.
    /// Os valores de modelo ("__SET_IN_ENV__" do appsettings, "<TODO>" do padrao) contam como segredo ausente.
    /// </summary>
    public class WebhookSignatureValidator : IWebhookSignatureValidator
    {
        private static readonly TimeSpan IntervaloDoAviso = TimeSpan.FromMinutes(5);
        private static DateTime _ultimoAviso = DateTime.MinValue;

        private readonly AutomationOptions _opcoes;
        private readonly ILogger<WebhookSignatureValidator>? _logger;

        public WebhookSignatureValidator(IOptions<AutomationOptions> options, ILogger<WebhookSignatureValidator>? logger = null)
        {
            _opcoes = options.Value;
            _logger = logger;
        }

        public static bool SegredoConfigurado(string? segredo) =>
            !string.IsNullOrWhiteSpace(segredo)
            && !segredo.TrimStart().StartsWith("__", StringComparison.Ordinal)   // "__SET_IN_ENV__" do appsettings
            && !segredo.TrimStart().StartsWith("<", StringComparison.Ordinal);   // "<TODO>" do valor padrao

        public bool ValidarXHubSignature256(string? cabecalhoAssinatura, string corpoRequisicao)
        {
            var segredo = _opcoes.Meta?.AppSecret;
            var temSegredo = SegredoConfigurado(segredo);

            if (!temSegredo)
            {
                if (_opcoes.StrictSignatureValidation)
                {
                    _logger?.LogError("[wa.in] ev=recusada motivo=sem_app_secret strict=true dica=\"configure Automation__Meta__AppSecret\"");
                    return false;
                }

                AvisarSemAssinatura();
                return true;
            }

            if (string.IsNullOrWhiteSpace(cabecalhoAssinatura)) return false;

            var esperado = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(segredo!), Encoding.UTF8.GetBytes(corpoRequisicao))).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(esperado), Encoding.ASCII.GetBytes(cabecalhoAssinatura.Trim().ToLowerInvariant()));
        }

        private void AvisarSemAssinatura()
        {
            if (DateTime.UtcNow - _ultimoAviso < IntervaloDoAviso) return;

            _ultimoAviso = DateTime.UtcNow;
            _logger?.LogError(
                "[wa.in] ev=assinatura_nao_verificada motivo=sem_app_secret risco=\"qualquer um pode enviar mensagens falsas\" " +
                "dica=\"configure Automation__Meta__AppSecret (App Secret do app na Meta)\"");
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
