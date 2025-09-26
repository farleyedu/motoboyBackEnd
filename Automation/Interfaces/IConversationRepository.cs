using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Models;

namespace APIBack.Automation.Interfaces
{
    public interface IConversationRepository
    {
        Task<Conversation?> ObterPorIdAsync(Guid id);
        Task<bool> InserirOuAtualizarAsync(Conversation conversa);
        Task DefinirModoAsync(Guid id, ModoConversa modo, int? agenteId);
        Task AcrescentarMensagemAsync(Message mensagem, string? phoneNumberId, string? idWa = null);
        Task<bool> ExisteIdMensagemPorProvedorWaAsync(string idMensagemWa);
        Task<Guid> GarantirClienteAsync(string telefoneE164, Guid idEstabelecimento);
        Task<Guid> ObterIdConversaPorClienteAsync(Guid idCliente, Guid idEstabelecimento);
        Task AtualizarEstadoAsync(Guid idConversa, EstadoConversa novoEstado);
        Task<IReadOnlyList<ConversationListItemDto>> ListarConversasAsync(string? estado, int? idAgente, bool incluirArquivadas);
        Task<ConversationHistoryDto?> ObterHistoricoConversaAsync(Guid idConversa, int page, int pageSize);
        Task<bool> AtribuirConversaAsync(Guid idConversa, int idAgente);
        Task<bool> FecharConversaAsync(Guid idConversa, int? idAgente, string? motivo);
        Task<ConversationDetailsDto?> ArquivarConversaAsync(Guid idConversa);
        Task<ConversationDetailsDto?> ObterDetalhesConversaAsync(Guid idConversa);
    }
}


