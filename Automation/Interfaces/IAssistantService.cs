// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Threading.Tasks;

namespace APIBack.Automation.Interfaces
{
    public interface IAssistantService
    {
        Task<string> GerarRespostaAsync(string textoUsuario, Guid idConversa, object? contexto = null);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

