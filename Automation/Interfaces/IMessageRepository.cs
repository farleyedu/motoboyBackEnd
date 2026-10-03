// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Threading.Tasks;
using APIBack.Automation.Models;

namespace APIBack.Automation.Interfaces
{
    public interface IMessageRepository
    {
        Task<bool> ExistsByProviderIdAsync(string providerMessageId);
        Task AddMessageAsync(Message mensagem, string? phoneNumberId, string? idWa);
        Task<IReadOnlyList<Message>> GetByConversationAsync(Guid idConversa, int limit = 200);
        Task AtualizarStatusAsync(Guid idMensagem, string status, string? codigoErro = null, string? mensagemErro = null);
        /// <summary>Recibo da Meta (entregue, lida, falhou) para a mensagem de saida com este id da Meta. False = mensagem desconhecida.</summary>
        Task<bool> AtualizarStatusPorProvedorAsync(string idProvedor, string status, string? codigoErro = null, string? mensagemErro = null);
        /// <summary>Grava o id que a Meta devolveu ao enviar, para os recibos acharem a mensagem.</summary>
        Task VincularProvedorAsync(Guid idMensagem, string idProvedor);
        Task<Guid?> ObterConversaDaMensagemAsync(Guid idMensagem);
        Task<Guid?> ObterConversaPorProvedorAsync(string idProvedor);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

