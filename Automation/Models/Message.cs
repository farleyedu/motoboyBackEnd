// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;

namespace APIBack.Automation.Models
{
    public enum DirecaoMensagem
    {
        Entrada = 0,
        Saida = 1
    }

    public class Message
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid IdConversa { get; set; }
        public string IdMensagemWa { get; set; } = string.Empty;
        public DirecaoMensagem Direcao { get; set; }
        public string Conteudo { get; set; } = string.Empty;
        public string? MetadadosMidia { get; set; }
        public DateTime DataHora { get; set; } = DateTime.UtcNow;
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
