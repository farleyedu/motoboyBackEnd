// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using APIBack.Automation.Models;

namespace APIBack.Automation.Services
{
    public sealed record ConversationIngressResult(
        Message Mensagem,
        bool ReiniciadaPorExpiracao,
        DateTime? AposEncerramentoManualEm = null,
        bool EmpresaPausada = false);
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
