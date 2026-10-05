using System;

namespace APIBack.Automation.Dtos
{
    public sealed class CreateConversationRequest
    {
        public Guid ClienteId { get; set; }
    }

    public sealed class CreateConversationResponse
    {
        public Guid ConversationId { get; set; }
        public Guid ClientId { get; set; }
    }
}
