// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Collections.Generic;

namespace APIBack.Automation.Dtos
{
    public record AssistantDecision(
        string Reply,
        string HandoverAction,
        string? AgentPrompt,
        bool ReservaConfirmada,
        HandoverContextDto? Detalhes,
        AssistantMedia? Media = null,
        IReadOnlyList<WhatsAppReplyButtonOption>? ReplyButtons = null
    );
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
