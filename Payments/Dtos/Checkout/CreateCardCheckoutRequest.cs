using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace APIBack.Payments.Dtos.Checkout
{
    public class CreateCardCheckoutRequest
    {
        [Range(0.01, 999999999.99)]
        public decimal Value { get; set; }

        [MaxLength(255)]
        public string? Description { get; set; }

        [Required]
        public CreditCardData CreditCard { get; set; } = new();

        [Required]
        public CreditCardHolderData HolderInfo { get; set; } = new();

        [MaxLength(64)]
        public string? RemoteIp { get; set; }
    }

    public class CreditCardData
    {
        [JsonPropertyName("holderName")]
        [Required]
        [MaxLength(120)]
        public string HolderName { get; set; } = string.Empty;

        [JsonPropertyName("number")]
        [Required]
        [MaxLength(20)]
        public string Number { get; set; } = string.Empty;

        [JsonPropertyName("expiryMonth")]
        [Required]
        [MaxLength(2)]
        public string ExpiryMonth { get; set; } = string.Empty;

        [JsonPropertyName("expiryYear")]
        [Required]
        [MaxLength(4)]
        public string ExpiryYear { get; set; } = string.Empty;

        [JsonPropertyName("ccv")]
        [Required]
        [MaxLength(4)]
        public string Ccv { get; set; } = string.Empty;
    }

    public class CreditCardHolderData
    {
        [JsonPropertyName("name")]
        [Required]
        [MaxLength(120)]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("email")]
        [Required]
        [EmailAddress]
        [MaxLength(180)]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("cpfCnpj")]
        [MaxLength(20)]
        public string? CpfCnpj { get; set; }

        [JsonPropertyName("postalCode")]
        [MaxLength(12)]
        public string? PostalCode { get; set; }

        [JsonPropertyName("addressNumber")]
        [MaxLength(20)]
        public string? AddressNumber { get; set; }

        [JsonPropertyName("phone")]
        [MaxLength(20)]
        public string? Phone { get; set; }
    }
}
