using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Model.AdminUsers;

namespace APIBack.Repository.Interface
{
    public interface IAdminUsuariosRepository
    {
        Task<AdminActorSnapshot?> ObterActorSnapshotAsync(int userId, Guid? estabelecimentoId, bool isSuperAdmin);
        Task<IReadOnlyCollection<AdminEstabelecimentoOption>> ListarEstabelecimentosGerenciaveisAsync(int userId, bool isSuperAdmin);
        Task<IReadOnlyCollection<AdminEmpresaOption>> ListarEmpresasAsync();
        Task<IReadOnlyCollection<AdminUsuarioListRow>> ListarUsuariosAsync(Guid empresaId, Guid estabelecimentoId);
        Task<IReadOnlyCollection<AdminTargetEstabelecimento>> ListarEstabelecimentosPorIdsAsync(IReadOnlyCollection<Guid> estabelecimentoIds);
        Task<bool> EmailExisteAsync(string email);
        Task<AdminCreatedUserSummary> CriarUsuarioAsync(AdminCreateUserCommand command);
    }
}
