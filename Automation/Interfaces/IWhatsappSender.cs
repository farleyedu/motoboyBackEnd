// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Threading.Tasks;

namespace APIBack.Automation.Interfaces
{
    public interface IWhatsappSender
    {
        Task<bool> EnviarTextoAsync(string idWa, string mensagem);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
