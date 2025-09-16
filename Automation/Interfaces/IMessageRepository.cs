// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Threading.Tasks;
using APIBack.Automation.Models;

namespace APIBack.Automation.Interfaces
{
    public interface IMessageRepository
    {
        Task<bool> ExistsByProviderIdAsync(string providerMessageId);
        Task AddMessageAsync(Message mensagem, string? phoneNumberId, string? idWa);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

