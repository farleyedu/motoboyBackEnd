using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Gestao;

namespace APIBack.Service.Interface
{
    public interface IGestaoService
    {
        Task<IReadOnlyCollection<GestaoEmpresaDto>> ListarEmpresasAsync(int userId, Guid? empresaId, Guid? estabelecimentoId, string? companyRole, string? establishmentRole, bool isSuperAdmin);
        Task<GestaoEmpresaDto> CriarEmpresaAsync(int userId, Guid? empresaId, Guid? estabelecimentoId, string? companyRole, string? establishmentRole, bool isSuperAdmin, SalvarEmpresaRequest request);
        Task<GestaoEmpresaDto> AtualizarEmpresaAsync(int userId, Guid? empresaId, Guid? estabelecimentoId, string? companyRole, string? establishmentRole, bool isSuperAdmin, Guid targetEmpresaId, SalvarEmpresaRequest request);
        Task AtualizarStatusEmpresaAsync(int userId, Guid? empresaId, Guid? estabelecimentoId, string? companyRole, string? establishmentRole, bool isSuperAdmin, Guid targetEmpresaId, bool ativa);
        Task AtualizarPausaEmpresaAsync(int userId, Guid? empresaId, Guid? estabelecimentoId, string? companyRole, string? establishmentRole, bool isSuperAdmin, Guid targetEmpresaId, bool pausada);
        Task<IReadOnlyCollection<GestaoContatoPausadoDto>> ListarContatosPausadosAsync(int userId, Guid? empresaId, Guid? estabelecimentoId, string? companyRole, string? establishmentRole, bool isSuperAdmin, Guid targetEmpresaId);

        Task<IReadOnlyCollection<GestaoEstabelecimentoDto>> ListarEstabelecimentosAsync(int userId, Guid? empresaId, Guid? estabelecimentoId, string? companyRole, string? establishmentRole, bool isSuperAdmin);
        Task<GestaoEstabelecimentoDto> CriarEstabelecimentoAsync(int userId, Guid? empresaId, Guid? estabelecimentoId, string? companyRole, string? establishmentRole, bool isSuperAdmin, SalvarEstabelecimentoRequest request);
        Task<GestaoEstabelecimentoDto> AtualizarEstabelecimentoAsync(int userId, Guid? empresaId, Guid? estabelecimentoId, string? companyRole, string? establishmentRole, bool isSuperAdmin, Guid targetEstabelecimentoId, SalvarEstabelecimentoRequest request);
        Task AtualizarStatusEstabelecimentoAsync(int userId, Guid? empresaId, Guid? estabelecimentoId, string? companyRole, string? establishmentRole, bool isSuperAdmin, Guid targetEstabelecimentoId, bool ativa);

        Task<IReadOnlyCollection<GestaoUsuarioDto>> ListarUsuariosAsync(int userId, Guid? empresaId, Guid? estabelecimentoId, string? companyRole, string? establishmentRole, bool isSuperAdmin);
        Task<GestaoUsuarioDto> CriarUsuarioAsync(int userId, Guid? empresaId, Guid? estabelecimentoId, string? companyRole, string? establishmentRole, bool isSuperAdmin, SalvarUsuarioRequest request);
        Task<GestaoUsuarioDto> AtualizarUsuarioAsync(int userId, Guid? empresaId, Guid? estabelecimentoId, string? companyRole, string? establishmentRole, bool isSuperAdmin, int targetUserId, SalvarUsuarioRequest request);
        Task AtualizarStatusUsuarioAsync(int userId, Guid? empresaId, Guid? estabelecimentoId, string? companyRole, string? establishmentRole, bool isSuperAdmin, int targetUserId, string status);
        Task RemoverUsuarioAsync(int userId, Guid? empresaId, Guid? estabelecimentoId, string? companyRole, string? establishmentRole, bool isSuperAdmin, int targetUserId);
    }
}
