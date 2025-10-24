using System;

namespace APIBack.Automation.Services
{
    public record WebhookProcessingEnvelope(
        ConversationProcessingInput Input,
        DateTime ReceivedAtUtc);
}
