namespace APIBack.Payments.Models
{
    public class CheckoutPaymentRecord
    {
        public long Id { get; set; }
        public int UserId { get; set; }
        public Guid? EstabelecimentoId { get; set; }
        public string AsaasPaymentId { get; set; } = string.Empty;
        public string? AsaasCustomerId { get; set; }
        public string PaymentType { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? AsaasStatus { get; set; }
        public decimal Value { get; set; }
        public string? Description { get; set; }
        public string? InvoiceUrl { get; set; }
        public string? PixQrCodeBase64 { get; set; }
        public string? PixCopyPaste { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
