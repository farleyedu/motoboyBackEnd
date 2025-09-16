// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Threading.Tasks;
using APIBack.Automation.Models;

namespace APIBack.Automation.Interfaces
{
    public interface IMessageService
    {
        Task<Message?> AdicionarMensagemAsync(Message mensagem, string? phoneNumberId, string? idWa);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

