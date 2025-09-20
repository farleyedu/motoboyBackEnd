// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
namespace APIBack.Automation.Dtos
{
    public record AssistantDecision(
        string Reply,
        string HandoverAction,
        string? AgentPrompt,
        bool ReservaConfirmada,
        HandoverContextDto? Detalhes
    );
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
