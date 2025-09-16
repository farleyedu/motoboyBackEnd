// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;

namespace APIBack.Automation.Dtos
{
    public class AssistantChatTurn
    {
        public string Role { get; set; } = "user"; // "user" | "assistant"
        public string Content { get; set; } = string.Empty;
        public DateTime? Timestamp { get; set; }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

