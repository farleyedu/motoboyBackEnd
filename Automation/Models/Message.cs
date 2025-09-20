// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;

namespace APIBack.Automation.Models
{
    public class Message
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid IdConversa { get; set; }
        public string IdMensagemWa { get; set; } = string.Empty;
        public DirecaoMensagem Direcao { get; set; }
        public string Conteudo { get; set; } = string.Empty;
        public string? MetadadosMidia { get; set; }
        public DateTime DataHora { get; set; } = DateTime.UtcNow;
        public string? Tipo { get; set; }
        public string? TipoOriginal { get; set; }
        public string? Status { get; set; }
        public string? IdProvedor { get; set; }
        public string? CodigoErro { get; set; }
        public string? MensagemErro { get; set; }
        public int Tentativas { get; set; } = 0;
        public string? CriadaPor { get; set; }
        public DateTime? DataEnvio { get; set; }
        public DateTime? DataEntrega { get; set; }
        public DateTime? DataLeitura { get; set; }
        public DateTime? DataCriacao { get; set; }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
