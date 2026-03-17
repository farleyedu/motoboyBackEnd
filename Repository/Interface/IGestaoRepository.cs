using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Gestao;
using APIBack.Model.Gestao;

namespace APIBack.Repository.Interface
{
    public interface IGestaoRepository
    {
        Task<IReadOnlyCollection<GestaoEmpresaRow>> ListarEmpresasAsync(Guid? empresaId);
        Task<GestaoEmpresaRow?> ObterEmpresaAsync(Guid empresaId);
        Task<Guid> CriarEmpresaAsync(SalvarEmpresaRequest request);
        Task<Guid> CriarEmpresaComEstabelecimentoAsync(SalvarEmpresaRequest request, SalvarEstabelecimentoRequest estabelecimentoRequest);
        Task AtualizarEmpresaAsync(Guid empresaId, SalvarEmpresaRequest request);
        Task AtualizarStatusEmpresaAsync(Guid empresaId, bool ativa);

        Task<IReadOnlyCollection<GestaoEstabelecimentoRow>> ListarEstabelecimentosAsync(Guid? empresaId, Guid? estabelecimentoId);
        Task<GestaoEstabelecimentoRow?> ObterEstabelecimentoAsync(Guid estabelecimentoId);
        Task<IReadOnlyCollection<GestaoTargetEstabelecimento>> ListarEstabelecimentosPorIdsAsync(IReadOnlyCollection<Guid> estabelecimentoIds);
        Task<Guid> CriarEstabelecimentoAsync(SalvarEstabelecimentoRequest request, Guid? empresaIdOverride = null);
        Task AtualizarEstabelecimentoAsync(Guid estabelecimentoId, SalvarEstabelecimentoRequest request);
        Task AtualizarStatusEstabelecimentoAsync(Guid estabelecimentoId, bool ativa);

        Task<IReadOnlyCollection<GestaoUsuarioResumoRow>> ListarUsuariosResumoAsync(Guid? empresaId, Guid? estabelecimentoId, bool incluirSuperAdminsGlobais);
        Task<IReadOnlyCollection<GestaoUsuarioEmpresaRow>> ListarUsuarioEmpresasAsync(Guid? empresaId, Guid? estabelecimentoId, bool incluirSuperAdminsGlobais);
        Task<IReadOnlyCollection<GestaoUsuarioEstabelecimentoRow>> ListarUsuarioEstabelecimentosAsync(Guid? empresaId, Guid? estabelecimentoId);
        Task<Dictionary<string, Dictionary<string, List<string>>>> ListarPermissoesPadraoPorTipoAsync(IReadOnlyCollection<string> tiposAcesso);
        Task<bool> EmailExisteAsync(string email, int? ignoreUserId = null);
        Task<GestaoUsuarioResumoRow?> ObterUsuarioResumoAsync(int userId);
        Task<IReadOnlyCollection<GestaoUsuarioEmpresaRow>> ObterUsuarioEmpresasAsync(int userId);
        Task<IReadOnlyCollection<GestaoUsuarioEstabelecimentoRow>> ObterUsuarioEstabelecimentosAsync(int userId);
        Task<int> SalvarUsuarioAsync(GestaoPersistenciaUsuarioCommand command);
        Task AtualizarStatusUsuarioAsync(int userId, bool ativo);
        Task RemoverUsuarioAsync(int userId);
    }
}
