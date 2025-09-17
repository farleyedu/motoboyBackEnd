// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;

namespace APIBack.Automation.Services
{
    public class AgenteService
    {
        private readonly IAgenteRepository _repo;

        public AgenteService(IAgenteRepository repo)
        {
            _repo = repo;
        }

        public Task<long?> ObterTelegramChatIdAsync(int agenteId)
            => _repo.ObterTelegramChatIdPorAgenteIdAsync(agenteId);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

