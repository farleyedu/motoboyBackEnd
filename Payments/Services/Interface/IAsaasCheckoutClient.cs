using APIBack.Payments.Dtos.Asaas;
using APIBack.Payments.Dtos.Checkout;

namespace APIBack.Payments.Services.Interface
{
    public interface IAsaasCheckoutClient
    {
        Task<AsaasCreateCustomerResponse> CreateCustomerAsync(AsaasCreateCustomerRequest request);
        Task<AsaasCreatePaymentResponse> CreatePixPaymentAsync(AsaasCreatePaymentRequest request);
        Task<AsaasCreatePaymentResponse> CreateCardPaymentAsync(AsaasCreateCardPaymentRequest request);
        Task<AsaasPixQrCodeResponse?> GetPixQrCodeAsync(string asaasPaymentId);
    }
}
