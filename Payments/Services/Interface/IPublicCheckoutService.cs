using APIBack.Payments.Dtos.Checkout;
using APIBack.Payments.Dtos.CheckoutPublic;

namespace APIBack.Payments.Services.Interface
{
    public interface IPublicCheckoutService
    {
        Task<CheckoutPaymentResponse> CreatePixAsync(CreatePublicPixCheckoutRequest request);
        Task<CheckoutPaymentResponse> CreateCardAsync(CreatePublicCardCheckoutRequest request, string? fallbackRemoteIp);
    }
}
