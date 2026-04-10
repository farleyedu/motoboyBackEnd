// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using APIBack.Automation.Dtos;
using APIBack.Automation.Models;

namespace APIBack.Automation.Services
{
    public record ConversationProcessingResult(
        bool ShouldIgnore,
        Message? MensagemRegistrada,
        Guid? IdConversa,
        IReadOnlyList<AssistantChatTurn> Historico,
        string? Contexto,
        HandoverContextDto HandoverDetalhes,
        string TextoUsuario,
        string? NumeroTelefoneExibicao,
        string? NumeroWhatsappId,
        string? AvisoRespostaInicial = null
    );
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
