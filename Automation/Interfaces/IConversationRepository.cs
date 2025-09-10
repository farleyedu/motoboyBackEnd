// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Threading.Tasks;
using APIBack.Automation.Models;

namespace APIBack.Automation.Interfaces
{
    public interface IConversationRepository
    {
        Task<Conversation?> ObterPorIdAsync(Guid id);
        Task InserirOuAtualizarAsync(Conversation conversa);
        Task DefinirModoAsync(Guid id, ModoConversa modo, string? agenteDesignado);
        Task AcrescentarMensagemAsync(Message mensagem);
        Task<bool> ExisteIdMensagemWaAsync(string idMensagemWa);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
