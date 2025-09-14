using System;
using System.Threading.Tasks;
using APIBack.Automation.Models;

namespace APIBack.Automation.Interfaces
{
    public interface IConversationRepository
    {
        Task<Conversation?> ObterPorIdAsync(Guid id);
        Task<bool> InserirOuAtualizarAsync(Conversation conversa);
        Task DefinirModoAsync(Guid id, ModoConversa modo, string? agenteDesignado);
        Task AcrescentarMensagemAsync(Message mensagem, string? phoneNumberId, string idWa = null);
        Task<bool> ExisteIdMensagemPorProvedorWaAsync(string idMensagemWa);
        Task<Guid> GarantirClienteAsync(string telefoneE164, Guid idEstabelecimento);

        // 👉 Adicione esta linha:
        Task<Guid> ObterIdConversaPorClienteAsync(Guid idCliente, Guid idEstabelecimento);
    }
}
