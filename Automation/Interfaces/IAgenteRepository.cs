// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Threading.Tasks;
using APIBack.Automation.Dtos;

namespace APIBack.Automation.Interfaces
{
    public interface IAgenteRepository
    {
        Task<long?> ObterTelegramChatIdPorAgenteIdAsync(int agenteId);
        Task<HandoverAgentDto?> ObterAgentePorIdAsync(int agenteId);
        Task<HandoverAgentDto?> ObterAgenteSuporteAsync();
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
