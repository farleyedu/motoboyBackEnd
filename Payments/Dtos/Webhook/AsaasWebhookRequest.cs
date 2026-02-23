using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace APIBack.Payments.Dtos.Webhook
{
    public class AsaasWebhookRequest
    {
        [Required]
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [Required]
        [JsonPropertyName("event")]
        public string Event { get; set; } = string.Empty;

        [JsonPropertyName("payment")]
        public AsaasWebhookPayment? Payment { get; set; }
    }

    public class AsaasWebhookPayment
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("billingType")]
        public string? BillingType { get; set; }

        [JsonPropertyName("value")]
        public decimal? Value { get; set; }
    }
}
