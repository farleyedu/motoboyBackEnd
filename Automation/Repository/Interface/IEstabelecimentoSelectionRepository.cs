using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Models;

namespace APIBack.Automation.Repository.Interface
{
    public interface IEstabelecimentoSelectionRepository
    {
        Task<UsuarioTenant?> ObterUsuarioAsync(int userId);
        Task<IReadOnlyCollection<UsuarioEstabelecimentoVinculo>> ListarEstabelecimentosUsuarioAsync(int userId);
        Task<IReadOnlyCollection<EstabelecimentoAtivoResumo>> ListarEstabelecimentosAtivosAsync();
        Task<EstabelecimentoDetalhe?> ObterEstabelecimentoDetalheAsync(Guid estabelecimentoId);
        Task<UsuarioEstabelecimentoAcesso?> ObterVinculoAsync(int userId, Guid estabelecimentoId);
        Task<UsuarioEmpresaAcesso?> ObterVinculoEmpresaAsync(int userId, Guid empresaId);
        Task<UsuarioEstabelecimentoAcesso?> ObterVinculoPorIdAsync(Guid vinculoId);
        Task AtualizarPermissoesCustomizadasAsync(Guid vinculoId, string? permissoesCustomizadasJson);
        Task AtualizarUltimoEstabelecimentoAsync(int userId, Guid estabelecimentoId);
        Task<Dictionary<string, List<string>>> ObterPermissoesPorTipoAsync(string? tipoAcesso);
    }
}
