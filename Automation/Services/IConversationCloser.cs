using System;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;

namespace APIBack.Automation.Services
{
    /// <summary>
    /// So o fechamento de conversa, do ConversationManagementService inteiro: deixa quem so precisa fechar (como o
    /// fechamento automatico ao concluir a entrega, em Service/ConversationAutoCloseService) depender de uma
    /// interface pequena, substituivel em teste sem montar o modulo de conversas inteiro.
    /// </summary>
    public interface IConversationCloser
    {
        Task<ConversationActionResponseDto> CloseAsync(
            Guid requestedConversationId,
            Guid idEstabelecimento,
            int? actorUserId,
            string? actorName,
            CloseConversationRequest? request);
    }
}
