using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;

namespace APIBack.Automation.Interfaces
{
    public interface IGaragemPainelRepository
    {
        Task<(IReadOnlyList<GarageLeadListItemDto> Itens, int Total)> ListarLeadsAsync(
            Guid idEstabelecimento,
            string? busca,
            string? status,
            string? objetivo,
            int pagina,
            int tamanhoPagina);

        Task<IReadOnlyList<GarageLeadStatusCountDto>> ContarLeadsPorStatusAsync(
            Guid idEstabelecimento,
            string? busca,
            string? objetivo);

        Task<GarageLeadDetailDto?> ObterLeadDetalheAsync(Guid idEstabelecimento, Guid idLead);

        Task<bool> AtualizarStatusLeadAsync(Guid idEstabelecimento, Guid idLead, string status);

        Task<GarageLeadSimulationDto?> CriarSimulacaoAsync(
            Guid idEstabelecimento,
            Guid idLead,
            CreateGarageLeadSimulationRequest request);

        Task<GarageLeadSimulationDto?> AtualizarSimulacaoAsync(
            Guid idEstabelecimento,
            Guid idLead,
            Guid idSimulacao,
            UpdateGarageLeadSimulationRequest request);

        Task<bool> RemoverSimulacaoAsync(Guid idEstabelecimento, Guid idLead, Guid idSimulacao);
    }
}
