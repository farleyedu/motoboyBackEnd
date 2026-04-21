using System;

namespace APIBack.Automation.Dtos
{
    public class ConversaAnexoResponseDto
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string? ContentType { get; set; }
        public long Tamanho { get; set; }
    }

    public class SendConversationMediaRequest
    {
        public string Url { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;
        public string? ContentType { get; set; }
        public string? Legenda { get; set; }
    }
}
