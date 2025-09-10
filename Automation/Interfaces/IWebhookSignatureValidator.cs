// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
namespace APIBack.Automation.Interfaces
{
    public interface IWebhookSignatureValidator
    {
        bool ValidarXHubSignature256(string? cabecalhoAssinatura, string corpoRequisicao);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
