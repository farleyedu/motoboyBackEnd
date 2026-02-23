using APIBack.Payments.Models;

namespace APIBack.Payments.Repository.Interface
{
    public interface ICheckoutRepository
    {
        Task<(string Nome, string Email)?> GetUserBasicAsync(int userId);
        Task<string?> GetAsaasCustomerIdAsync(int userId);
        Task UpsertAsaasCustomerIdAsync(int userId, string asaasCustomerId);
        Task<long> CreatePaymentAsync(NewCheckoutPayment payment);
        Task<CheckoutPaymentRecord?> GetPaymentByIdAsync(long paymentId);
        Task<CheckoutPaymentRecord?> GetPaymentByAsaasIdAsync(string asaasPaymentId);
        Task UpdatePaymentFromWebhookAsync(string asaasPaymentId, string status, string? asaasStatus, string? webhookPayloadJson);
        Task<bool> TryCreateWebhookLogAsync(string eventId, string eventType, string? asaasPaymentId, string payloadJson);
        Task CompleteWebhookLogAsync(string eventId, bool success, string? errorMessage);
    }
}
