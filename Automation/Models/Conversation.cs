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
        public Guid IdEstabelecimento { get; set; }
        public Guid IdCliente { get; set; }
        public string IdWa { get; set; } = string.Empty;
        public ModoConversa Modo { get; set; } = ModoConversa.Bot;
        public string? AgenteDesignado { get; set; }
        public DateTime? UltimoUsuarioEm { get; set; }
        public DateTime? Janela24hExpiraEm { get; set; }
        public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
        public DateTime? AtualizadoEm { get; set; } = DateTime.UtcNow;
        public string? MessageIdWhatsapp { get; set; }
        
        // Propriedades para compatibilidade com SqlConversationRepository
        public DateTime DataPrimeiraMensagem => CriadoEm;
        public DateTime DataUltimaMensagem => AtualizadoEm ?? CriadoEm;
        public string Canal => "whatsapp";
        public string Estado => "aberta";

    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
