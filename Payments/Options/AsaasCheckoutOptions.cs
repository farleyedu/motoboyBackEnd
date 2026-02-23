namespace APIBack.Payments.Options
{
    public class AsaasCheckoutOptions
    {
        public string ApiKey { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = "https://sandbox.asaas.com/api/";
        public string WebhookToken { get; set; } = string.Empty;
        public string Environment { get; set; } = "sandbox";
    }
}
