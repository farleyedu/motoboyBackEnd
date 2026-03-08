using System;
using System.Collections.Generic;

namespace APIBack.Automation.Dtos
{
    public class ConversationActionResponseDto
    {
        public ConversationDetailsDto? Conversa { get; set; }
        public ConversationControlDto? Controle { get; set; }
        public IReadOnlyList<ConversationEventDto> Eventos { get; set; } = Array.Empty<ConversationEventDto>();
        public ConversationMessageItemDto? Mensagem { get; set; }
    }
}
