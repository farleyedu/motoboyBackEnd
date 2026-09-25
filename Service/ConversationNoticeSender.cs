using System;
using System.Threading.Tasks;
using APIBack.Automation.Services;

namespace APIBack.Service
{
    /// <summary>Envia os avisos de rastreio pela conversa do cliente (mensagem marcada como "sistema").</summary>
    public sealed class ConversationNoticeSender : ITrackingNoticeSender
    {
        private readonly ConversationManagementService _conversations;

        public ConversationNoticeSender(ConversationManagementService conversations)
        {
            _conversations = conversations;
        }

        public Task<Guid> SendAsync(Guid conversaId, Guid estabelecimentoId, string text) =>
            _conversations.SendSystemNoticeAsync(conversaId, estabelecimentoId, text);
    }
}
