using System.ComponentModel.DataAnnotations;

namespace APIBack.Payments.Dtos.Checkout
{
    public class CreatePixCheckoutRequest
    {
        [Range(0.01, 999999999.99)]
        public decimal Value { get; set; }

        [MaxLength(255)]
        public string? Description { get; set; }

        public DateTime? DueDate { get; set; }
    }
}
