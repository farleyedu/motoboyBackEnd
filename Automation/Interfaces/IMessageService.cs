// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Threading.Tasks;
using APIBack.Automation.Models;

namespace APIBack.Automation.Interfaces
{
    public interface IMessageService
    {
        Task<Message?> AdicionarMensagemAsync(Message mensagem, string? phoneNumberId, string? idWa);
        Task AtualizarStatusAsync(Guid idMensagem, string status, string? codigoErro = null, string? mensagemErro = null);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

