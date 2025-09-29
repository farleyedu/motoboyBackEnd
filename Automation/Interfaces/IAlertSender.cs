// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Threading.Tasks;

namespace APIBack.Automation.Interfaces
{
    public interface IAlertSender
    {
        Task EnviarAlertaTelegramAsync(string mensagem, string? chatIdOverride = null);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
