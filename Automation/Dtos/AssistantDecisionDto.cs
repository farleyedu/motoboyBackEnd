// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
namespace APIBack.Automation.Dtos
{
    // Estrutura de decisão retornada pela IA para handover
    public class AssistantDecisionDto
    {
        public string? reply { get; set; }
        public string? handover { get; set; } // none | ask | confirm
        public string? agent_prompt { get; set; }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

