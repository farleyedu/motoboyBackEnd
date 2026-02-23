namespace APIBack.Payments.Dtos.Checkout
{
    public class CheckoutPaymentStatusResponse
    {
        public long PaymentId { get; set; }
        public string AsaasPaymentId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? AsaasStatus { get; set; }
        public decimal Value { get; set; }
        public string PaymentType { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
