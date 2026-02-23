using System.Text.Json;
using APIBack.Payments.Dtos.Asaas;
using APIBack.Payments.Dtos.Checkout;
using APIBack.Payments.Models;
using APIBack.Payments.Repository.Interface;
using APIBack.Payments.Services.Interface;
using Microsoft.Extensions.Logging;

namespace APIBack.Payments.Services
{
    public class CheckoutPaymentService : ICheckoutPaymentService
    {
        private readonly ICheckoutRepository _repository;
        private readonly IAsaasCheckoutClient _asaasClient;
        private readonly ILogger<CheckoutPaymentService> _logger;

        public CheckoutPaymentService(
            ICheckoutRepository repository,
            IAsaasCheckoutClient asaasClient,
            ILogger<CheckoutPaymentService> logger)
        {
            _repository = repository;
            _asaasClient = asaasClient;
            _logger = logger;
        }

        public async Task<CheckoutPaymentResponse> CreatePixAsync(int userId, Guid? estabelecimentoId, CreatePixCheckoutRequest request)
        {
            var customerId = await EnsureAsaasCustomerAsync(userId);

            var dueDate = (request.DueDate ?? DateTime.UtcNow.AddDays(1)).Date;
            var asaasResponse = await _asaasClient.CreatePixPaymentAsync(new AsaasCreatePaymentRequest
            {
                Customer = customerId,
                Value = request.Value,
                Description = string.IsNullOrWhiteSpace(request.Description) ? "Checkout PIX" : request.Description.Trim(),
                DueDate = dueDate.ToString("yyyy-MM-dd")
            });

            AsaasPixQrCodeResponse? pixQrCode = null;
            try
            {
                pixQrCode = await _asaasClient.GetPixQrCodeAsync(asaasResponse.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao buscar QR Code PIX no Asaas. PaymentId={AsaasPaymentId}", asaasResponse.Id);
            }

            var paymentId = await _repository.CreatePaymentAsync(new NewCheckoutPayment
            {
                UserId = userId,
                EstabelecimentoId = estabelecimentoId,
                AsaasPaymentId = asaasResponse.Id,
                AsaasCustomerId = customerId,
                PaymentType = "pix",
                Status = "pending",
                AsaasStatus = asaasResponse.Status,
                Value = request.Value,
                Description = request.Description,
                InvoiceUrl = asaasResponse.InvoiceUrl,
                PixQrCodeBase64 = pixQrCode?.EncodedImage,
                PixCopyPaste = pixQrCode?.Payload,
                GatewayResponseJson = JsonSerializer.Serialize(asaasResponse)
            });

            return new CheckoutPaymentResponse
            {
                PaymentId = paymentId,
                AsaasPaymentId = asaasResponse.Id,
                PaymentType = "pix",
                Status = "pending",
                Value = request.Value,
                DueDate = asaasResponse.DueDate,
                InvoiceUrl = asaasResponse.InvoiceUrl,
                PixQrCodeBase64 = pixQrCode?.EncodedImage,
                PixCopyPaste = pixQrCode?.Payload,
                Message = "Pagamento PIX gerado com sucesso."
            };
        }

        public async Task<CheckoutPaymentResponse> CreateCardAsync(int userId, Guid? estabelecimentoId, CreateCardCheckoutRequest request)
        {
            var customerId = await EnsureAsaasCustomerAsync(userId);

            var asaasResponse = await _asaasClient.CreateCardPaymentAsync(new AsaasCreateCardPaymentRequest
            {
                Customer = customerId,
                Value = request.Value,
                Description = string.IsNullOrWhiteSpace(request.Description) ? "Checkout Cartao" : request.Description.Trim(),
                DueDate = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd"),
                CreditCard = request.CreditCard,
                CreditCardHolderInfo = request.HolderInfo,
                RemoteIp = request.RemoteIp
            });

            var paymentId = await _repository.CreatePaymentAsync(new NewCheckoutPayment
            {
                UserId = userId,
                EstabelecimentoId = estabelecimentoId,
                AsaasPaymentId = asaasResponse.Id,
                AsaasCustomerId = customerId,
                PaymentType = "credit_card",
                Status = "pending",
                AsaasStatus = asaasResponse.Status,
                Value = request.Value,
                Description = request.Description,
                InvoiceUrl = asaasResponse.InvoiceUrl,
                GatewayResponseJson = JsonSerializer.Serialize(asaasResponse)
            });

            return new CheckoutPaymentResponse
            {
                PaymentId = paymentId,
                AsaasPaymentId = asaasResponse.Id,
                PaymentType = "credit_card",
                Status = "pending",
                Value = request.Value,
                DueDate = asaasResponse.DueDate,
                InvoiceUrl = asaasResponse.InvoiceUrl,
                Message = "Pagamento com cartao criado. Aguardando confirmacao do webhook."
            };
        }

        public async Task<CheckoutPaymentStatusResponse?> GetStatusAsync(long paymentId, int userId, bool isSuperAdmin)
        {
            var payment = await _repository.GetPaymentByIdAsync(paymentId);
            if (payment == null)
            {
                return null;
            }

            if (!isSuperAdmin && payment.UserId != userId)
            {
                throw new UnauthorizedAccessException("Pagamento nao pertence ao usuario autenticado.");
            }

            return new CheckoutPaymentStatusResponse
            {
                PaymentId = payment.Id,
                AsaasPaymentId = payment.AsaasPaymentId,
                Status = payment.Status,
                AsaasStatus = payment.AsaasStatus,
                Value = payment.Value,
                PaymentType = payment.PaymentType,
                CreatedAt = payment.CreatedAt,
                UpdatedAt = payment.UpdatedAt
            };
        }

        private async Task<string> EnsureAsaasCustomerAsync(int userId)
        {
            var existing = await _repository.GetAsaasCustomerIdAsync(userId);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            var user = await _repository.GetUserBasicAsync(userId);
            if (user == null)
            {
                throw new InvalidOperationException("Usuario nao encontrado para checkout.");
            }

            var created = await _asaasClient.CreateCustomerAsync(new AsaasCreateCustomerRequest
            {
                Name = string.IsNullOrWhiteSpace(user.Value.Nome) ? $"Usuario {userId}" : user.Value.Nome,
                Email = user.Value.Email
            });

            await _repository.UpsertAsaasCustomerIdAsync(userId, created.Id);
            return created.Id;
        }
    }
}
