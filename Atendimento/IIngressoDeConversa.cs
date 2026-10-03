using System;
using System.Threading.Tasks;
using APIBack.Automation.Services;

namespace APIBack.Atendimento
{
    /// <summary>Grava a mensagem do cliente na conversa da loja (cria cliente e conversa quando preciso). Existe como interface para o pipeline ser testado sem banco.</summary>
    public interface IIngressoDeConversa
    {
        /// <returns>Nulo quando a mensagem e duplicada ou a empresa esta desativada.</returns>
        Task<ConversationIngressResult?> AcrescentarEntradaAsync(
            string idWa,
            string idMensagemWa,
            string conteudo,
            string displayPhoneNumber,
            string? phoneNumberId = null,
            DateTime? dataMensagemUtc = null,
            string? tipoOrigem = null,
            string? telefoneContato = null,
            Guid? idEstabelecimentoDoCanal = null,
            Guid? idCanal = null);
    }
}
