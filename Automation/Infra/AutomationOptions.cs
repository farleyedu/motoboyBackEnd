// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
namespace APIBack.Automation.Infra
{
    public class AutomationOptions
    {
        public bool StrictSignatureValidation { get; set; } = false;
        public string VerifyToken { get; set; } = "<TODO>";
        public MetaOptions Meta { get; set; } = new();
        public TelegramOptions Telegram { get; set; } = new();
        public HandoverOptions Handover { get; set; } = new();
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

    public class HandoverOptions
    {
        // Base da API interna para acionar handover (ex.: http://127.0.0.1:7137/automation)
        public string BaseUrl { get; set; } = "http://127.0.0.1:7137/automation";
        // ID padrão do agente humano (tabela agentes.id)
        public int? DefaultAgentId { get; set; }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

