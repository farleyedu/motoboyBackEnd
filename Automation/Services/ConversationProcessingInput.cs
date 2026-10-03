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
        string? TextoInterpretado = null,
        Guid? IdEstabelecimento = null,
        Guid? IdCanal = null
    );
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
