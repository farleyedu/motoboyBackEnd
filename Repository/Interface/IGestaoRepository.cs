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
        /// <summary>Empresa, loja, servicos padrao e gerente na mesma transacao: se algo falhar, nada fica criado.</summary>
        Task<GestaoProvisionamentoResultado> CriarEmpresaComEstabelecimentoAsync(
            SalvarEmpresaRequest request, SalvarEstabelecimentoRequest estabelecimentoRequest, GestaoGerentePadraoCommand? gerente);
        Task AtualizarEmpresaAsync(Guid empresaId, SalvarEmpresaRequest request);
        Task AtualizarStatusEmpresaAsync(Guid empresaId, bool ativa);
        Task AtualizarPausaEmpresaAsync(Guid empresaId, bool pausada);
        Task<IReadOnlyCollection<GestaoContatoPausadoRow>> ListarContatosPausadosAsync(Guid empresaId);

        Task<IReadOnlyCollection<GestaoEstabelecimentoRow>> ListarEstabelecimentosAsync(Guid? empresaId, Guid? estabelecimentoId);
        Task<GestaoEstabelecimentoRow?> ObterEstabelecimentoAsync(Guid estabelecimentoId);
        Task<IReadOnlyCollection<GestaoTargetEstabelecimento>> ListarEstabelecimentosPorIdsAsync(IReadOnlyCollection<Guid> estabelecimentoIds);
        /// <summary>Loja, servicos padrao e acesso dos gerentes da empresa na mesma transacao.</summary>
        Task<GestaoProvisionamentoResultado> CriarEstabelecimentoAsync(SalvarEstabelecimentoRequest request, GestaoGerentePadraoCommand? gerente);
        /// <summary>Loja antiga: liga os servicos padrao que faltam e garante que ha gerente com acesso (vincula ou cria o padrao).</summary>
        /// <exception cref="KeyNotFoundException">A loja nao existe.</exception>
        Task<GestaoProvisionamentoResultado> CompletarCadastroAsync(Guid estabelecimentoId, GestaoGerentePadraoCommand gerente);
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

        Task<MotoboyPerfilRow?> ObterPerfilMotoboyAsync(int motoboyId);
        Task<IReadOnlyCollection<MotoboyPerfilVinculoRow>> ListarVinculosMotoboyAsync(int motoboyId);
        Task AtualizarPerfilMotoboyAsync(int motoboyId, MotoboyPerfilUpdateCommand command);
    }
}
