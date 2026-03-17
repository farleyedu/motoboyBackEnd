using System;
using System.Threading.Tasks;
using APIBack.DTOs.AdminUsers;

namespace APIBack.Service.Interface
{
    public interface IAdminUsuariosService
    {
        Task<AdminUsuariosContextoResponse> ObterContextoAsync(int userId, Guid? estabelecimentoId, bool isSuperAdmin);
        Task<AdminUsuariosListResponse> ListarUsuariosAsync(int userId, Guid? estabelecimentoId, bool isSuperAdmin);
        Task<AdminUsuarioListItemDto> CriarUsuarioAsync(int userId, Guid? empresaIdContexto, Guid? estabelecimentoIdContexto, bool isSuperAdmin, CreateAdminUsuarioRequest request);
    }
}
