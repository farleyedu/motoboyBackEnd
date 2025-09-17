// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
namespace APIBack.Automation.Dtos
{
    public class HandoverRequest
    {
        public HandoverAgentDto? Agente { get; set; }
        public bool ReservaConfirmada { get; set; } = false;
        public HandoverContextDto? Detalhes { get; set; }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
