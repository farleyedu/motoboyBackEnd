// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;

namespace APIBack.Automation.Interfaces
{
    public interface IWhatsAppTokenProvider
    {
        string? GetAccessToken();
        void SetAccessToken(string token);
        DateTimeOffset? LastUpdatedUtc { get; }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
