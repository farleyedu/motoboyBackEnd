// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;

namespace APIBack.Automation.Models
{
    public enum ModoConversa
    {
        Bot = 0,
        Humano = 1
    }

    public enum EstadoConversa
    {
        Aberto = 0,
        FechadoAutomaticamente = 1,
        FechadoAgente = 2,
        Arquivada = 3,
        EmAtendimento = 4
    }

    public class Conversation
    {
        public Guid IdConversa { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public Guid IdCliente { get; set; }
        public string IdWa { get; set; } = string.Empty;
        public ModoConversa Modo { get; set; } = ModoConversa.Bot;
        public int? AgenteDesignadoId { get; set; }
        public DateTime? UltimoUsuarioEm { get; set; }
        public DateTime? Janela24hExpiraEm { get; set; }
        public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
        public DateTime? AtualizadoEm { get; set; } = DateTime.UtcNow;
        public string? MessageIdWhatsapp { get; set; }
        public EstadoConversa Estado { get; set; } = EstadoConversa.Aberto;

        // Propriedades para compatibilidade com SqlConversationRepository
        public DateTime DataPrimeiraMensagem => CriadoEm;
        public DateTime DataUltimaMensagem => AtualizadoEm ?? CriadoEm;
        public string Canal => "whatsapp";
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

