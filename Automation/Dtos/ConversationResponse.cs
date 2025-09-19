// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using APIBack.Automation.Models;

namespace APIBack.Automation.Dtos
{
    public class ConversationResponse
    {
        public Guid IdConversa { get; set; }
        public string IdWa { get; set; } = string.Empty;
        public ModoConversa Modo { get; set; }
        public int? AgenteDesignadoId { get; set; }
        public DateTime UltimoUsuarioEm { get; set; }
        public DateTime? Janela24hExpiraEm { get; set; }
        public DateTime CriadoEm { get; set; }
        public DateTime AtualizadoEm { get; set; }
        public List<ConversationMessageView> Mensagens { get; set; } = new();
    }

    public class ConversationMessageView
    {
        public string IdMensagemWa { get; set; } = string.Empty;
        public string Direcao { get; set; } = string.Empty; // "Entrada" | "Saida"
        public string Conteudo { get; set; } = string.Empty;
        public string? MetadadosMidia { get; set; }
        public DateTime DataHora { get; set; }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

