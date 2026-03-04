using System;
using System.Threading.Tasks;
using APIBack.Automation.Models;

namespace APIBack.Automation.Interfaces
{
    public interface IGaragemLeadRepository
    {
        Task<GarageLead?> ObterLeadAbertoAsync(Guid idEstabelecimento, string telefoneE164);
        Task<GarageLead?> ObterPorConversaAsync(Guid idConversa);
        Task<Guid> CriarAsync(GarageLead lead);
        Task AtualizarAsync(GarageLead lead);
        Task ConcluirAsync(GarageLead lead);
    }
}
