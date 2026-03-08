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
        Task<HandoverAgentDto?> ObterAgentePorUsuarioIdAsync(int usuarioId);
        Task<IReadOnlyList<ConversationAgentDto>> ListarAgentesAsync();
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
