// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Threading.Tasks;

namespace APIBack.Automation.Interfaces
{
    public interface IAgenteRepository
    {
        Task<long?> ObterTelegramChatIdPorAgenteIdAsync(int agenteId);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

