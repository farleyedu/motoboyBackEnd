using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.CRM;

namespace APIBack.Service.Interface
{
    public interface ICrmService
    {
        Task<IReadOnlyCollection<CrmOportunidadeResumoDto>> ListarOportunidadesAsync(string? status, string? tipo, int? responsavelUsuarioId, string? busca);
        Task<CrmOportunidadeDetalheDto?> ObterOportunidadeAsync(Guid oportunidadeId);
        Task<CrmOportunidadeResumoDto> CriarOportunidadeAsync(int usuarioId, CriarCrmOportunidadeRequest request);
        /// <summary>Novo lead com proximo passo obrigatorio. Parecidos exigem decisao humana (409 DUPLICADO); a mesma chave devolve o mesmo lead.</summary>
        Task<CrmLeadCriadoDto> CriarLeadAsync(int usuarioId, CriarCrmLeadRequest request, string? chaveIdempotencia, System.Threading.CancellationToken ct = default);
        Task<IReadOnlyList<CrmLeadCandidatoDto>> ListarLeadsParecidosAsync(string? nome, string? telefone, System.Threading.CancellationToken ct = default);
        Task<CrmOportunidadeResumoDto> AtualizarOportunidadeAsync(int usuarioId, Guid oportunidadeId, AtualizarCrmOportunidadeRequest request);
        Task<CrmOportunidadeResumoDto> MoverOportunidadeAsync(int usuarioId, Guid oportunidadeId, MoverCrmOportunidadeRequest request);
        Task<IReadOnlyCollection<CrmCandidatoAssociacaoDto>> ListarCandidatosAssociacaoAsync(Guid oportunidadeId, string? cnpj, string? nomeEmpresa);
        Task<IReadOnlyCollection<CrmResponsavelDto>> ListarResponsaveisAsync();
        Task<FecharCrmOportunidadeResponse> FecharOportunidadeAsync(int usuarioId, Guid oportunidadeId, FecharCrmOportunidadeRequest request);

        Task<IReadOnlyCollection<CrmContratoResumoDto>> ListarContratosAsync(string? status);
        Task<CrmContratoDetalheDto?> ObterContratoAsync(Guid contratoId);
        Task<CrmContratoResumoDto> AtualizarContratoAsync(int usuarioId, Guid contratoId, AtualizarCrmContratoRequest request);
        Task<CrmLancamentoDto> RegistrarPagamentoAsync(int usuarioId, RegistrarCrmLancamentoRequest request);

        Task<CrmFinanceiroResumoDto> ObterResumoFinanceiroAsync(DateTime? referenciaMes);
        Task<IReadOnlyCollection<CrmLancamentoDto>> ListarLancamentosFinanceiroAsync(DateTime? referenciaMes);
        Task<CrmDivisaoFinanceiraDto> ObterDivisaoFinanceiraAsync(DateTime? referenciaMes);
        Task<IReadOnlyCollection<string>> ListarMesesAtivosAsync();
    }
}
