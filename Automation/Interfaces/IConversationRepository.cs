using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Models;

namespace APIBack.Automation.Interfaces
{
    public interface IConversationRepository
    {
        Task<Conversation?> ObterPorIdAsync(Guid id, Guid? idEstabelecimento = null);
        Task<bool> InserirOuAtualizarAsync(Conversation conversa);
        Task DefinirModoAsync(Guid id, ModoConversa modo, int? agenteId);
        Task AcrescentarMensagemAsync(Message mensagem, string? phoneNumberId, string? idWa = null);
        Task<bool> ExisteIdMensagemPorProvedorWaAsync(string idMensagemWa);
        Task<Guid> GarantirClienteAsync(string telefoneE164, Guid idEstabelecimento);
        Task<Guid> ObterIdConversaPorClienteAsync(Guid idCliente, Guid idEstabelecimento);
        Task<Guid> ObterIdConversaAbertaPorGrupoAsync(Guid idConversaGrupo, Guid idEstabelecimento);
        Task AtualizarEstadoAsync(Guid idConversa, EstadoConversa novoEstado);
        Task<IReadOnlyList<ConversationListItemDto>> ListarConversasAsync(string? estado, int? idAgente, bool incluirArquivadas, Guid? idEstabelecimento = null);
        Task<ConversationHistoryDto?> ObterHistoricoConversaAsync(Guid idConversa, int page, int pageSize, Guid? idEstabelecimento = null);
        Task<bool> AtribuirConversaAsync(Guid idConversa, int idAgente, Guid? idEstabelecimento = null);
        Task<bool> FecharConversaAsync(Guid idConversa, int? idAgente, string? motivo, Guid? idEstabelecimento = null, string? tipoFechamento = null);
        Task<ConversationDetailsDto?> ArquivarConversaAsync(Guid idConversa, Guid? idEstabelecimento = null);
        Task<ConversationDetailsDto?> ObterDetalhesConversaAsync(Guid idConversa, Guid? idEstabelecimento = null);
        Task<bool> AtualizarStatusAtendimentoAsync(Guid idConversa, string status, int? idAgente, string? agenteNome, Guid? idEstabelecimento = null);
        Task<bool> VoltarParaBotAsync(Guid idConversa, Guid? idEstabelecimento = null);
        Task<bool> ReabrirConversaAsync(Guid idConversa, Guid? idEstabelecimento = null);
        Task<bool> MarcarComoLidaAsync(Guid idConversa, Guid? idEstabelecimento = null);
        Task<ConversationControlDto?> ObterControleConversaAsync(Guid idConversa, Guid? idEstabelecimento = null);
        Task RegistrarEventoAsync(Guid idConversa, ConversationEventDto evento, Guid? idEstabelecimento = null);
        Task<IReadOnlyList<ConversationEventDto>> ListarEventosConversaAsync(Guid idConversa, Guid? idEstabelecimento = null);
        Task<IReadOnlyList<ConversationAgentDto>> ListarAgentesAsync(Guid? idEstabelecimento = null);
        Task SalvarContextoAsync(Guid idConversa, ConversationContext contexto);
        Task<ConversationContext?> ObterContextoAsync(Guid idConversa);
        Task LimparContextoAsync(Guid idConversa);
    }
}


