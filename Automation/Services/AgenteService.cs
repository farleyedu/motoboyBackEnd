// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Infra;
using APIBack.Automation.Interfaces;
using Microsoft.Extensions.Options;

namespace APIBack.Automation.Services
{
    public class AgenteService
    {
        private readonly IAgenteRepository _repo;
        private readonly AutomationOptions _options;

        public AgenteService(IAgenteRepository repo, IOptions<AutomationOptions> options)
        {
            _repo = repo;
            _options = options?.Value ?? new AutomationOptions();
        }

        public Task<long?> ObterTelegramChatIdAsync(int agenteId)
            => _repo.ObterTelegramChatIdPorAgenteIdAsync(agenteId);

        public async Task<HandoverAgentDto?> ObterAgenteSuporteAsync()
        {
            var agenteSuporte = await _repo.ObterAgenteSuporteAsync();
            if (agenteSuporte != null)
            {
                return agenteSuporte;
            }

            var defaultId = _options.Handover?.DefaultAgentId;
            if (defaultId.HasValue)
            {
                return await _repo.ObterAgentePorIdAsync(defaultId.Value);
            }

            return null;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================