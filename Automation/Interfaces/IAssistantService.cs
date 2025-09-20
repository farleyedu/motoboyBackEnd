// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;

namespace APIBack.Automation.Interfaces
{
    public interface IAssistantService
    {
        Task<AssistantDecision> GerarDecisaoAsync(string textoUsuario, Guid idConversa, object? contexto = null);
        Task<AssistantDecision> GerarDecisaoComHistoricoAsync(Guid idConversa, string textoUsuario, IEnumerable<AssistantChatTurn> historico, object? contexto = null);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
