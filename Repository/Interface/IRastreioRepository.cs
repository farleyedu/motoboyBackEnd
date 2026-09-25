using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Rastreio;
using APIBack.Service;

namespace APIBack.Repository.Interface
{
    public interface IRastreioRepository
    {
        Task<NoticeSettings> GetSettingsAsync(Guid estabelecimentoId);
        Task<NoticeSettings> UpsertSettingsAsync(Guid estabelecimentoId, int actorUserId, NoticeSettings settings);

        Task<bool> SetOptInAsync(Guid estabelecimentoId, int pedidoId, bool optIn, string origem);
        Task<RastreioPedidoDto> GetPedidoAsync(Guid estabelecimentoId, int pedidoId);
        Task<bool> SetMotoboySharingAsync(int motoboyId, bool shares);
        Task<bool> GetMotoboySharingAsync(int motoboyId);

        Task<long?> TryReserveAsync(int pedidoId, string tipo);
        Task MarkAsync(long id, string status, string? motivo, Guid? mensagemId);
        Task<bool> ReleaseForResendAsync(Guid estabelecimentoId, int pedidoId, string tipo);
        Task<IReadOnlyList<NoticeCandidate>> GetCandidatesAsync();

        Task<string> EnsureTokenAsync(int pedidoId);
        Task<PublicTrackingSource?> GetPublicSourceAsync(string token);
        Task InvalidateTokenAsync(int pedidoId);
    }
}
