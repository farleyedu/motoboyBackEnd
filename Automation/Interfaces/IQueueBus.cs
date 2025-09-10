// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Threading.Tasks;
using APIBack.Automation.Models;

namespace APIBack.Automation.Interfaces
{
    public interface IQueueBus
    {
        Task PublicarEntradaAsync(Message mensagem);
        Task PublicarSaidaAsync(Message mensagem);
        Task PublicarAlertaAsync(string mensagem);
        Task PublicarDeadLetterAsync(string payload);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
