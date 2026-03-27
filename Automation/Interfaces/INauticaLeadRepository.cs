using System;
using System.Threading.Tasks;
using APIBack.Automation.Models;

namespace APIBack.Automation.Interfaces
{
    public interface INauticaLeadRepository
    {
        Task<NauticaLead?> ObterLeadAbertoAsync(Guid idEstabelecimento, string telefoneE164);
        Task<NauticaLead?> ObterPorConversaAsync(Guid idConversa);
        Task<Guid> CriarAsync(NauticaLead lead);
        Task AtualizarAsync(NauticaLead lead);
        Task ConcluirAsync(NauticaLead lead);
        Task DesqualificarAsync(NauticaLead lead, string status);
        Task CancelarLeadAbertoAsync(Guid idEstabelecimento, string telefoneE164);
    }
}
