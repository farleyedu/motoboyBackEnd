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
        public string AccessToken { get; set; } = "EAAJSsCDzZCQwBPQi7ZBWKUPZAArWy9lzBMFlxtfdnxDHUXNjnqCV7ZBUZBym1ryXGxtlBRAQLicjsEnd3mIMiwnbj1dswxfZAshXjWEiaDWmsbLiLmxwQYkcQcvkVPpvDbkjYvNlmkolvYRolXI64LEHnqaQtKeqqHlIv1hS6MWxQmJPeJB29weHZAzfgPu5iIZBtLfC8AsPqW0aTB8VElYRo4MFaUFaqZBc6daZCJYEcwwYQ2m0CMYNL9uCisF1eI4QZDZD";
        public string PhoneNumberId { get; set; } = "<TODO>";
    }

    public class TelegramOptions
    {
        public string BotToken { get; set; } = "<TODO>";
        public string ChatId { get; set; } = "<TODO>";
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

