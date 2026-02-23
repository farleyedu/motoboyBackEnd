using APIBack.Payments.Dtos.Asaas;
using APIBack.Payments.Dtos.Checkout;
using APIBack.Payments.Dtos.CheckoutPublic;
using APIBack.Payments.Services.Interface;

namespace APIBack.Payments.Services
{
    public class PublicCheckoutService : IPublicCheckoutService
    {
        private readonly IAsaasCheckoutClient _asaasClient;

        public PublicCheckoutService(IAsaasCheckoutClient asaasClient)
        {
            _asaasClient = asaasClient;
        }

        public async Task<CheckoutPaymentResponse> CreatePixAsync(CreatePublicPixCheckoutRequest request)
        {
            var customerId = await CreateCustomerAsync(request.Customer);
            var dueDate = (request.DueDate ?? DateTime.UtcNow.AddDays(1)).Date;

            var asaasResponse = await _asaasClient.CreatePixPaymentAsync(new AsaasCreatePaymentRequest
            {
                Customer = customerId,
                Value = request.Value,
                Description = string.IsNullOrWhiteSpace(request.Description) ? "Checkout PIX" : request.Description.Trim(),
                DueDate = dueDate.ToString("yyyy-MM-dd")
            });

            var pixQrCode = await _asaasClient.GetPixQrCodeAsync(asaasResponse.Id);

            return new CheckoutPaymentResponse
            {
                PaymentId = 0,
                AsaasPaymentId = asaasResponse.Id,
                PaymentType = "pix",
                Status = string.IsNullOrWhiteSpace(asaasResponse.Status) ? "pending" : asaasResponse.Status!,
                Value = request.Value,
                DueDate = asaasResponse.DueDate,
                InvoiceUrl = asaasResponse.InvoiceUrl,
                PixQrCodeBase64 = pixQrCode?.EncodedImage,
                PixCopyPaste = pixQrCode?.Payload,
                Message = "Pagamento PIX gerado com sucesso."
            };
        }

        public async Task<CheckoutPaymentResponse> CreateCardAsync(CreatePublicCardCheckoutRequest request, string? fallbackRemoteIp)
        {
            var customerId = await CreateCustomerAsync(request.Customer);
            var dueDate = (request.DueDate ?? DateTime.UtcNow.AddDays(1)).Date;

            var asaasResponse = await _asaasClient.CreateCardPaymentAsync(new AsaasCreateCardPaymentRequest
            {
                Customer = customerId,
                Value = request.Value,
                Description = string.IsNullOrWhiteSpace(request.Description) ? "Checkout Cartao" : request.Description.Trim(),
                DueDate = dueDate.ToString("yyyy-MM-dd"),
                CreditCard = request.CreditCard,
                CreditCardHolderInfo = request.HolderInfo,
                RemoteIp = string.IsNullOrWhiteSpace(request.RemoteIp) ? fallbackRemoteIp : request.RemoteIp
            });

            return new CheckoutPaymentResponse
            {
                PaymentId = 0,
                AsaasPaymentId = asaasResponse.Id,
                PaymentType = "credit_card",
                Status = string.IsNullOrWhiteSpace(asaasResponse.Status) ? "pending" : asaasResponse.Status!,
                Value = request.Value,
                DueDate = asaasResponse.DueDate,
                InvoiceUrl = asaasResponse.InvoiceUrl,
                Message = "Pagamento com cartao criado."
            };
        }

        private async Task<string> CreateCustomerAsync(PublicCheckoutCustomerRequest customer)
        {
            var created = await _asaasClient.CreateCustomerAsync(new AsaasCreateCustomerRequest
            {
                Name = customer.Name,
                Email = customer.Email,
                CpfCnpj = customer.CpfCnpj,
                Phone = customer.Phone
            });

            return created.Id;
        }
    }
}
