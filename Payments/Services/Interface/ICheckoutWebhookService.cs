using APIBack.Payments.Dtos.Webhook;

namespace APIBack.Payments.Services.Interface
{
    public interface ICheckoutWebhookService
    {
        Task<WebhookProcessResponse> ProcessAsaasAsync(AsaasWebhookRequest request, string? webhookTokenHeader);
    }
}
