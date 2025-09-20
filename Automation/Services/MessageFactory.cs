// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using APIBack.Automation.Models;

namespace APIBack.Automation.Services
{
    public static class MessageFactory
    {
        public static Message CreateMessage(Guid idConversa, string conteudo, DirecaoMensagem direcao, string criadaPor, string? idMensagemWa = null, string? tipoOrigem = null)
        {
            var agora = DateTime.UtcNow;
            var tipoBanco = MessageTypeMapper.MapType(tipoOrigem, direcao, criadaPor);
            var message = new Message
            {
                IdConversa = idConversa,
                Conteudo = conteudo,
                Direcao = direcao,
                CriadaPor = criadaPor,
                DataHora = agora,
                DataCriacao = agora,
                Status = direcao == DirecaoMensagem.Entrada ? "recebida" : "fila",
                IdMensagemWa = idMensagemWa ?? $"local-{Guid.NewGuid():N}",
                Tipo = tipoBanco,
                TipoOriginal = tipoOrigem
            };

            if (direcao == DirecaoMensagem.Saida)
            {
                message.DataEnvio = agora;
            }

            return message;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
