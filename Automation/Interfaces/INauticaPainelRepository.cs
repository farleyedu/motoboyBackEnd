using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;

namespace APIBack.Automation.Interfaces
{
    public interface INauticaPainelRepository
    {
        Task<(IReadOnlyList<NauticaLeadListItemDto> Itens, int Total)> ListarLeadsAsync(
            Guid idEstabelecimento,
            string? busca,
            string? status,
            int pagina,
            int tamanhoPagina);

        Task<IReadOnlyList<NauticaLeadStatusCountDto>> ContarLeadsPorStatusAsync(
            Guid idEstabelecimento,
            string? busca);

        Task<NauticaLeadDetailDto?> ObterLeadDetalheAsync(Guid idEstabelecimento, Guid idLead);

        Task<bool> AtualizarStatusLeadAsync(Guid idEstabelecimento, Guid idLead, string status);

        Task<PromoverContatoPausadoResponseDto?> PromoverContatoPausadoAsync(Guid idEstabelecimento, Guid idConversa);
    }
}
