using APIBack.Payments.Dtos.Checkout;

namespace APIBack.Payments.Services.Interface
{
    public interface ICheckoutPaymentService
    {
        Task<CheckoutPaymentResponse> CreatePixAsync(int userId, Guid? estabelecimentoId, CreatePixCheckoutRequest request);
        Task<CheckoutPaymentResponse> CreateCardAsync(int userId, Guid? estabelecimentoId, CreateCardCheckoutRequest request);
        Task<CheckoutPaymentStatusResponse?> GetStatusAsync(long paymentId, int userId, bool isSuperAdmin);
    }
}
