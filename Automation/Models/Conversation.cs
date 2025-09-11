// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;

namespace APIBack.Automation.Models
{
    public enum ModoConversa
    {
        Bot = 0,
        Humano = 1
    }

    public class Conversation
    {
        public Guid IdConversa { get; set; }
        public string IdWa { get; set; } = string.Empty;
        public ModoConversa Modo { get; set; } = ModoConversa.Bot;
        public string? AgenteDesignado { get; set; }
        public DateTime? UltimoUsuarioEm { get; set; }
        public DateTime? Janela24hExpiraEm { get; set; }
        public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
        public DateTime? AtualizadoEm { get; set; } = DateTime.UtcNow;
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
