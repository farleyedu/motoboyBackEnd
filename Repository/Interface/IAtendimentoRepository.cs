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

        Task<MotoboyMessageDto> SendMotoboyMessageAsync(Guid estabelecimentoId, int motoboyId, int? pedidoId, string direction, string body, string? quickKey, int? actorUserId);
        Task<IReadOnlyList<MotoboyMessageDto>> ListMotoboyMessagesAsync(Guid estabelecimentoId, int? motoboyId, int? pedidoId, int limit);
        Task<int> MarkReadAsync(Guid estabelecimentoId, int motoboyId, string readerSide);
    }
}
