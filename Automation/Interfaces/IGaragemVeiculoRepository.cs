using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;

namespace APIBack.Automation.Interfaces
{
    public interface IGaragemVeiculoRepository
    {
        Task<(IReadOnlyList<GarageVehicleAdminDto> Items, int Total)> ListarAsync(
            Guid idEstabelecimento,
            string? status,
            string? categoria,
            string? search,
            int page,
            int pageSize);

        Task<GarageVehicleAdminDto?> ObterPorIdAsync(
            Guid id,
            Guid? idEstabelecimento = null,
            bool somenteDisponiveis = false);

        Task<GarageVehicleAdminDto> CriarAsync(
            Guid idEstabelecimento,
            CreateGarageVehicleRequest request);

        Task<GarageVehicleAdminDto?> AtualizarAsync(
            Guid id,
            Guid idEstabelecimento,
            UpsertGarageVehicleRequest request);

        Task<bool> RemoverAsync(
            Guid id,
            Guid? idEstabelecimento = null);

        Task<bool> AtualizarStatusAsync(
            Guid id,
            string status,
            Guid? idEstabelecimento = null);

        Task<bool> AtualizarDestaqueAsync(
            Guid id,
            bool destaque,
            string? label,
            Guid? idEstabelecimento = null);

        Task<GarageVehicleShowcaseResponseDto> ObterVitrineAsync(
            Guid idEstabelecimento,
            string? categoria,
            string? search);

        Task<GarageVehicleMetricsDto> ObterMetricasAsync(Guid idEstabelecimento);

        Task<GarageVehicleDto?> ObterPorSlugPublicoAsync(Guid idEstabelecimento, string slug);

        Task<string?> ObterVitrineBaseUrlAsync(Guid idEstabelecimento);

        Task AtualizarVitrineBaseUrlAsync(Guid idEstabelecimento, string url);
    }
}
