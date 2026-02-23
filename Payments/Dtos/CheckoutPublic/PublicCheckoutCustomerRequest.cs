using System.ComponentModel.DataAnnotations;

namespace APIBack.Payments.Dtos.CheckoutPublic
{
    public class PublicCheckoutCustomerRequest
    {
        [Required]
        [MaxLength(120)]
        public string Name { get; set; } = string.Empty;

        [EmailAddress]
        [MaxLength(180)]
        public string? Email { get; set; }

        [MaxLength(20)]
        public string? CpfCnpj { get; set; }

        [MaxLength(20)]
        public string? Phone { get; set; }
    }
}
