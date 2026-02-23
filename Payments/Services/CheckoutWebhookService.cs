using System.Text.Json;
using APIBack.Payments.Dtos.Webhook;
using APIBack.Payments.Options;
using APIBack.Payments.Repository.Interface;
using APIBack.Payments.Services.Interface;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace APIBack.Payments.Services
{
    public class CheckoutWebhookService : ICheckoutWebhookService
    {
        private readonly ICheckoutRepository _repository;
        private readonly AsaasCheckoutOptions _options;
        private readonly ILogger<CheckoutWebhookService> _logger;

        public CheckoutWebhookService(
            ICheckoutRepository repository,
            IOptions<AsaasCheckoutOptions> options,
            ILogger<CheckoutWebhookService> logger)
        {
            _repository = repository;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<WebhookProcessResponse> ProcessAsaasAsync(AsaasWebhookRequest request, string? webhookTokenHeader)
        {
            if (string.IsNullOrWhiteSpace(_options.WebhookToken) ||
                string.IsNullOrWhiteSpace(webhookTokenHeader) ||
                !string.Equals(_options.WebhookToken, webhookTokenHeader, StringComparison.Ordinal))
            {
                return WebhookProcessResponse.Error("Token de webhook invalido.", "UNAUTHORIZED");
            }

            if (string.IsNullOrWhiteSpace(request.Id))
            {
                return WebhookProcessResponse.Error("EventId obrigatorio.", "INVALID_EVENT");
            }

            var payloadJson = JsonSerializer.Serialize(request);
            var createdLog = await _repository.TryCreateWebhookLogAsync(
                request.Id,
                request.Event ?? string.Empty,
                request.Payment?.Id,
                payloadJson);

            if (!createdLog)
            {
                return WebhookProcessResponse.Ignore(request.Id, "Evento ja processado anteriormente.");
            }

            try
            {
                if (string.IsNullOrWhiteSpace(request.Payment?.Id))
                {
                    await _repository.CompleteWebhookLogAsync(request.Id, false, "PaymentId ausente.");
                    return WebhookProcessResponse.Error("PaymentId ausente no webhook.", "INVALID_PAYMENT");
                }

                var payment = await _repository.GetPaymentByAsaasIdAsync(request.Payment.Id);
                if (payment == null)
                {
                    await _repository.CompleteWebhookLogAsync(request.Id, true, null);
                    return WebhookProcessResponse.Ignore(request.Id, "Pagamento nao encontrado localmente.");
                }

                var newStatus = MapStatus(request.Event, payment.PaymentType);
                if (!string.IsNullOrWhiteSpace(newStatus))
                {
                    await _repository.UpdatePaymentFromWebhookAsync(
                        request.Payment.Id,
                        newStatus,
                        request.Payment.Status,
                        payloadJson);
                }

                await _repository.CompleteWebhookLogAsync(request.Id, true, null);
                return WebhookProcessResponse.Ok(request.Id, request.Payment.Id, newStatus, "Webhook processado.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao processar webhook Asaas. EventId={EventId}", request.Id);
                await _repository.CompleteWebhookLogAsync(request.Id, false, ex.Message);
                return WebhookProcessResponse.Error("Erro ao processar webhook.", "PROCESSING_ERROR");
            }
        }

        private static string? MapStatus(string? eventName, string? paymentType)
        {
            if (string.IsNullOrWhiteSpace(eventName))
            {
                return null;
            }

            return eventName switch
            {
                "PAYMENT_CONFIRMED" when string.Equals(paymentType, "credit_card", StringComparison.OrdinalIgnoreCase) => "approved",
                "PAYMENT_RECEIVED" => "approved",
                "PAYMENT_AWAITING_RISK_ANALYSIS" => "awaiting_approval",
                "PAYMENT_REPROVED_BY_RISK_ANALYSIS" => "refused",
                "PAYMENT_OVERDUE" => "overdue",
                "PAYMENT_REFUNDED" => "refunded",
                "PAYMENT_DELETED" => "refused",
                _ => null
            };
        }
    }
}
