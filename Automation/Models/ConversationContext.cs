using System;
using System.Collections.Generic;

namespace APIBack.Automation.Models
{
    public class ConversationContext
    {
        public string? Estado { get; set; }
        public Dictionary<string, object>? DadosColetados { get; set; }
        public DateTime? ExpiracaoEstado { get; set; }
        public long? ReservaIdPendente { get; set; }
        public string? AcaoPendente { get; set; }
    }
}
