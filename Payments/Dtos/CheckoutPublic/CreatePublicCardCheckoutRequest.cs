using System.ComponentModel.DataAnnotations;
using APIBack.Payments.Dtos.Checkout;

namespace APIBack.Payments.Dtos.CheckoutPublic
{
    public class CreatePublicCardCheckoutRequest
    {
        [Range(0.01, 999999999.99)]
        public decimal Value { get; set; }

        [MaxLength(255)]
        public string? Description { get; set; }

        public DateTime? DueDate { get; set; }

        [Required]
        public PublicCheckoutCustomerRequest Customer { get; set; } = new();

        [Required]
        public CreditCardData CreditCard { get; set; } = new();

        [Required]
        public CreditCardHolderData HolderInfo { get; set; } = new();

        [MaxLength(64)]
        public string? RemoteIp { get; set; }
    }
}
