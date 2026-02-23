namespace APIBack.Payments.Dtos.Webhook
{
    public class WebhookProcessResponse
    {
        public bool Success { get; set; }
        public bool Ignored { get; set; }
        public string? EventId { get; set; }
        public string? AsaasPaymentId { get; set; }
        public string? NewStatus { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? ErrorCode { get; set; }

        public static WebhookProcessResponse Ok(string? eventId, string? asaasPaymentId, string? status, string message)
            => new()
            {
                Success = true,
                EventId = eventId,
                AsaasPaymentId = asaasPaymentId,
                NewStatus = status,
                Message = message
            };

        public static WebhookProcessResponse Ignore(string? eventId, string message)
            => new()
            {
                Success = true,
                Ignored = true,
                EventId = eventId,
                Message = message
            };

        public static WebhookProcessResponse Error(string message, string? errorCode = null)
            => new()
            {
                Success = false,
                Message = message,
                ErrorCode = errorCode
            };
    }
}
