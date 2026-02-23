using System.Text.Json.Serialization;

namespace APIBack.Payments.Dtos.Asaas
{
    public class AsaasCreateCustomerRequest
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("cpfCnpj")]
        public string? CpfCnpj { get; set; }

        [JsonPropertyName("phone")]
        public string? Phone { get; set; }
    }

    public class AsaasCreateCustomerResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }

    public class AsaasCreatePaymentRequest
    {
        [JsonPropertyName("customer")]
        public string Customer { get; set; } = string.Empty;

        [JsonPropertyName("billingType")]
        public string BillingType { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public decimal Value { get; set; }

        [JsonPropertyName("dueDate")]
        public string DueDate { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }

    public class AsaasCreateCardPaymentRequest : AsaasCreatePaymentRequest
    {
        [JsonPropertyName("creditCard")]
        public object CreditCard { get; set; } = new();

        [JsonPropertyName("creditCardHolderInfo")]
        public object CreditCardHolderInfo { get; set; } = new();

        [JsonPropertyName("remoteIp")]
        public string? RemoteIp { get; set; }
    }

    public class AsaasCreatePaymentResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("invoiceUrl")]
        public string? InvoiceUrl { get; set; }

        [JsonPropertyName("dueDate")]
        public DateTime? DueDate { get; set; }
    }

    public class AsaasPixQrCodeResponse
    {
        [JsonPropertyName("encodedImage")]
        public string? EncodedImage { get; set; }

        [JsonPropertyName("payload")]
        public string? Payload { get; set; }
    }

    public class AsaasErrorResponse
    {
        [JsonPropertyName("errors")]
        public List<AsaasErrorItem>? Errors { get; set; }
    }

    public class AsaasErrorItem
    {
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }
}
