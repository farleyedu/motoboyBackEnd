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
        public string AccessToken { get; set; } = "<TODO>";
        public string PhoneNumberId { get; set; } = "<TODO>";
    }

    public class TelegramOptions
    {
        public string BotToken { get; set; } = "<TODO>";
        public string ChatId { get; set; } = "<TODO>";
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

