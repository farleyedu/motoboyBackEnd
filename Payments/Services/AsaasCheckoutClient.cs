using System.Net.Http.Json;
using System.Text.Json;
using System.Linq;
using APIBack.Payments.Dtos.Asaas;
using APIBack.Payments.Services.Interface;
using Microsoft.Extensions.Logging;

namespace APIBack.Payments.Services
{
    public class AsaasCheckoutClient : IAsaasCheckoutClient
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<AsaasCheckoutClient> _logger;
        private readonly JsonSerializerOptions _jsonOptions;

        public AsaasCheckoutClient(HttpClient httpClient, ILogger<AsaasCheckoutClient> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };
        }

        public async Task<AsaasCreateCustomerResponse> CreateCustomerAsync(AsaasCreateCustomerRequest request)
        {
            var response = await _httpClient.PostAsJsonAsync("v3/customers", request, _jsonOptions);
            var body = response.Content.ReadAsStringAsync();
            return await HandleResponse<AsaasCreateCustomerResponse>(response, "create customer");
        }

        public async Task<AsaasCreatePaymentResponse> CreatePixPaymentAsync(AsaasCreatePaymentRequest request)
        {
            request.BillingType = "PIX";
            var response = await _httpClient.PostAsJsonAsync("v3/payments", request, _jsonOptions);
            return await HandleResponse<AsaasCreatePaymentResponse>(response, "create pix payment");
        }

        public async Task<AsaasCreatePaymentResponse> CreateCardPaymentAsync(AsaasCreateCardPaymentRequest request)
        {
            request.BillingType = "CREDIT_CARD";
            var response = await _httpClient.PostAsJsonAsync("v3/payments", request, _jsonOptions);
            return await HandleResponse<AsaasCreatePaymentResponse>(response, "create card payment");
        }

        public async Task<AsaasPixQrCodeResponse?> GetPixQrCodeAsync(string asaasPaymentId)
        {
            if (string.IsNullOrWhiteSpace(asaasPaymentId))
            {
                return null;
            }

            var response = await _httpClient.GetAsync($"v3/payments/{asaasPaymentId}/pixQrCode");
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<AsaasPixQrCodeResponse>(_jsonOptions);
        }

        private async Task<T> HandleResponse<T>(HttpResponseMessage response, string operation)
        {
            if (response.IsSuccessStatusCode)
            {
                var data = await response.Content.ReadFromJsonAsync<T>(_jsonOptions);
                if (data == null)
                {
                    throw new InvalidOperationException($"Asaas returned empty response for {operation}.");
                }

                return data;
            }

            var body = await response.Content.ReadAsStringAsync();
            var message = ParseAsaasError(body);
            _logger.LogWarning("Asaas {Operation} failed. Status={StatusCode} Message={Message}", operation, response.StatusCode, message);
            throw new InvalidOperationException($"Asaas error while {operation}: {message}");
        }

        private string ParseAsaasError(string? body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return "empty response";
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<AsaasErrorResponse>(body, _jsonOptions);
                if (parsed?.Errors == null || parsed.Errors.Count == 0)
                {
                    return body;
                }

                return string.Join(" | ", parsed.Errors.Select(e => $"{e.Code}: {e.Description}"));
            }
            catch
            {
                return body.Length > 300 ? body[..300] : body;
            }
        }
    }
}
