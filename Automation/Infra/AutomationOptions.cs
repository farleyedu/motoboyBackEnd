// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
namespace APIBack.Automation.Infra
{
    public class AutomationOptions
    {
        public bool StrictSignatureValidation { get; set; } = false;
        public MetaOptions Meta { get; set; } = new();
        public TelegramOptions Telegram { get; set; } = new();
    }

    public class MetaOptions
    {
        public string AppSecret { get; set; } = "<TODO>";
        public string AccessToken { get; set; } = "EAAJSsCDzZCQwBPcaWN9JRs0bdGZBVHWvqRDM0L5ZBCqzxQAu7r4gbrQdHwXeCZCi9YzGsWAYMaKbBIJWL0viWxCIDZA1V47jPREKiXxJaGrZBvJhfPFQZCXDniHDRF2ZB3JWjZCIBvZCm9OWsviOxrEMX3b29lToNEuX0paxLD65M4yTKWFFEtYIdFQkHUHHunhPZAvcVENszPRh64vkWwksvdfG8LBKYQtOZBCHpEeoLMjQ75ss4yZC4zgVfPWnwmWlPfAZDZD";
        public string PhoneNumberId { get; set; } = "<TODO>";
    }

    public class TelegramOptions
    {
        public string BotToken { get; set; } = "<TODO>";
        public string ChatId { get; set; } = "<TODO>";
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

