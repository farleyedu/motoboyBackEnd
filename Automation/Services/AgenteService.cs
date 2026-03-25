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

        public Task<HandoverAgentDto?> ObterAgentePorIdAsync(int agenteId)
            => _repo.ObterAgentePorIdAsync(agenteId);

        public Task<HandoverAgentDto?> ObterAgentePorUsuarioIdAsync(int usuarioId)
            => _repo.ObterAgentePorUsuarioIdAsync(usuarioId);

        public Task<IReadOnlyList<ConversationAgentDto>> ListarAgentesAsync()
            => _repo.ListarAgentesAsync();

        public Task EnsureAgenteAsync(int usuarioId)
            => _repo.EnsureAgenteAsync(usuarioId);

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
