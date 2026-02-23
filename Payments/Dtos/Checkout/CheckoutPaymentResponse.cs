namespace APIBack.Payments.Dtos.Checkout
{
    public class CheckoutPaymentResponse
    {
        public long PaymentId { get; set; }
        public string AsaasPaymentId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal Value { get; set; }
        public string PaymentType { get; set; } = string.Empty;
        public DateTime? DueDate { get; set; }
        public string? InvoiceUrl { get; set; }
        public string? PixQrCodeBase64 { get; set; }
        public string? PixCopyPaste { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}
