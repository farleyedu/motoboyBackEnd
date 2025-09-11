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
        Task<bool> ExisteIdMensagemPorProvedorWaAsync(string idMensagemWa); // idempotência por id_provedor (wamid)
    }
}
