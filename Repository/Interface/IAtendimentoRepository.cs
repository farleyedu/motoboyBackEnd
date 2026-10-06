using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Atendimento;

namespace APIBack.Repository.Interface
{
    public interface IAtendimentoRepository
    {
        Task<AtendimentoConfigDto> GetConfigAsync(Guid estabelecimentoId);
        Task<AtendimentoConfigDto> UpsertConfigAsync(Guid estabelecimentoId, int actorUserId, UpdateAtendimentoConfigRequest request);

        Task<IReadOnlyList<RespostaRapidaDto>> ListRespostasAsync(Guid estabelecimentoId, bool onlyActive);
        Task<RespostaRapidaDto> CreateRespostaAsync(Guid estabelecimentoId, SalvarRespostaRapidaRequest request);
        Task<RespostaRapidaDto?> UpdateRespostaAsync(Guid estabelecimentoId, Guid id, SalvarRespostaRapidaRequest request);
        Task<bool> DeleteRespostaAsync(Guid estabelecimentoId, Guid id);
        Task<IReadOnlyDictionary<string, string?>> GetPedidoVariablesAsync(Guid estabelecimentoId, int? pedidoId);

        Task<ConversaDoPedidoDto> GetConversaDoPedidoAsync(Guid estabelecimentoId, int pedidoId);
        Task<ConversaDoPedidoDto> VincularAsync(Guid estabelecimentoId, Guid conversaId, int pedidoId);
        Task<ConversaDoPedidoDto> AbrirConversaDoPedidoAsync(Guid estabelecimentoId, int pedidoId);

        /// <summary>
        /// Garante uma conversa pro cliente que acabou de ser cadastrado manualmente (sem nenhuma mensagem
        /// ainda), pra ele aparecer na lista de conversas do atendimento. Idempotente: se ja existe conversa
        /// pra esse cliente, devolve o id dela sem criar outra.
        /// </summary>
        Task<Guid> EnsureConversaParaClienteAsync(Guid estabelecimentoId, Guid clienteId);

        Task<MotoboyMessageDto> SendMotoboyMessageAsync(Guid estabelecimentoId, int motoboyId, int? pedidoId, string direction, string body, string? quickKey, int? actorUserId);
        Task<IReadOnlyList<MotoboyMessageDto>> ListMotoboyMessagesAsync(Guid estabelecimentoId, int? motoboyId, int? pedidoId, int limit);
        Task<int> MarkReadAsync(Guid estabelecimentoId, int motoboyId, string readerSide);

        /// <summary>Nome do motoboy (linha canonica), para rotular mensagens que ele origina. Null se nao encontrado.</summary>
        Task<string?> ObterNomeMotoboyAsync(int motoboyId);

        /// <summary>Motoboys com vinculo ativo nesta loja (contatos), online primeiro.</summary>
        Task<IReadOnlyList<MotoboyRosterEntryDto>> ListMotoboysVinculadosAsync(Guid estabelecimentoId);

        Task<IReadOnlyList<MotoboyGroupMessageDto>> ListGroupMessagesAsync(Guid estabelecimentoId, int limit);
        Task<MotoboyGroupMessageDto> SendGroupMessageAsync(Guid estabelecimentoId, string senderType, int? motoboyId, int? sentByUserId, string body);
    }
}
