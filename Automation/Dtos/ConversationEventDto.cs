using System;
using System.Collections.Generic;

namespace APIBack.Automation.Dtos
{
    public class ConversationEventDto
    {
        public Guid Id { get; set; }
        public string Type { get; set; } = string.Empty;
        public DateTime At { get; set; }
        public string? ActorName { get; set; }
        public string? FromStatus { get; set; }
        public string? ToStatus { get; set; }
        public string? Reason { get; set; }
        public string? CloseType { get; set; }
        public string? Source { get; set; }
        public int? ActorUserId { get; set; }
        public int? ActorAgentId { get; set; }
        public Dictionary<string, object?>? Data { get; set; }
    }
}
