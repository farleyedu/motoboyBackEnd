using System;

namespace APIBack.Automation.Dtos
{
    public class ConversationControlDto
    {
        public Guid ConversationId { get; set; }
        public Guid ClientId { get; set; }
        public Guid ConversationGroupId { get; set; }
        public string Status { get; set; } = "com_bot";
        public bool CanBotReply { get; set; }
        public bool CanManualReply { get; set; }
        public string? SendBlockReasonCode { get; set; }
        public int? AssignedAgentId { get; set; }
        public string? AssignedAgentName { get; set; }
        public DateTime? LastInteractionAt { get; set; }
        public DateTime? AutoCloseAt { get; set; }
        public int UnreadCount { get; set; }
    }
}
