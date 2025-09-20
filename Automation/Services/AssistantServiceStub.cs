// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;

namespace APIBack.Automation.Services
{
    // Stub usado em cenários de teste/desenvolvimento sem chamada real à IA
    public class AssistantServiceStub : IAssistantService
    {
        public Task<AssistantDecision> GerarDecisaoAsync(string textoUsuario, Guid idConversa, object? contexto = null)
        {
            var reply = string.IsNullOrWhiteSpace(textoUsuario)
                ? "Poderia repetir?"
                : $"[STUB] Você disse: '{textoUsuario}'.";

            return Task.FromResult(new AssistantDecision(reply, "none", null, false, null));
        }

        public Task<AssistantDecision> GerarDecisaoComHistoricoAsync(Guid idConversa, string textoUsuario, IEnumerable<AssistantChatTurn> historico, object? contexto = null)
            => GerarDecisaoAsync(textoUsuario, idConversa, contexto);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
