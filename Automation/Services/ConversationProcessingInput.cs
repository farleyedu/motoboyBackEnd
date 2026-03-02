// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using APIBack.Automation.Dtos;

namespace APIBack.Automation.Services
{
    public record ConversationProcessingInput(
        WebhookMessageDto Mensagem,
        string Texto,
        string? PhoneNumberDisplay,
        string? PhoneNumberId,
        DateTime? DataMensagemUtc,
        WebhookChangeValueDto Valor,
        string? TextoInterpretado = null
    );
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
