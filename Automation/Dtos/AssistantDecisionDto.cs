// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
namespace APIBack.Automation.Dtos
{
    // Estrutura utilizada para desserializar respostas da IA quando em JSON
    public class AssistantDecisionDto
    {
        public string? reply { get; set; }
        public string? handover { get; set; } // none | ask | confirm
        public string? handoverAction { get; set; }
        public string? handover_action { get; set; }
        public string? agent_prompt { get; set; }
        public bool? reserva_confirmada { get; set; }
        public string? nome_completo { get; set; }
        public int? qtd_pessoas { get; set; }
        public string? data { get; set; }
        public string? hora { get; set; }
        public HandoverContextDto? detalhes { get; set; }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
