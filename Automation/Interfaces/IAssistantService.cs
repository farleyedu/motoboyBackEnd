// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;

namespace APIBack.Automation.Interfaces
{
    public interface IAssistantService
    {
        Task<string> GerarRespostaAsync(string textoUsuario, Guid idConversa, object? contexto = null);
        Task<string> GerarRespostaComHistoricoAsync(Guid idConversa, string textoUsuario, IEnumerable<AssistantChatTurn> historico, object? contexto = null);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
