// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Threading.Tasks;

namespace APIBack.Automation.Interfaces
{
    public interface IAlertSender
    {
        Task EnviarAlertaAsync(string mensagem);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
