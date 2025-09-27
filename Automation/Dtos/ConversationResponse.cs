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
        public string Id { get; set; } = string.Empty;
        public string SenderId { get; set; } = string.Empty;
        public string Msg { get; set; } = string.Empty;
        public string Type { get; set; } = "text";
        public DateTime CreatedAt { get; set; }

        public static ConversationMessageView FromMessage(Message mensagem)
        {
            if (mensagem == null) throw new ArgumentNullException(nameof(mensagem));

            return new ConversationMessageView
            {
                Id = !string.IsNullOrWhiteSpace(mensagem.IdMensagemWa) ? mensagem.IdMensagemWa : mensagem.Id.ToString(),
                SenderId = mensagem.Direcao == DirecaoMensagem.Entrada ? "in" : "out",
                Msg = mensagem.Conteudo ?? string.Empty,
                Type = ResolveType(mensagem),
                CreatedAt = mensagem.DataHora
            };
        }

        private static string ResolveType(Message mensagem)
        {
            if (!string.IsNullOrWhiteSpace(mensagem.Tipo))
            {
                return mensagem.Tipo!.ToLowerInvariant();
            }

            return string.IsNullOrWhiteSpace(mensagem.MetadadosMidia) ? "text" : "image";
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

