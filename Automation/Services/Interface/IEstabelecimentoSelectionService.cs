using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Dtos.Estabelecimentos;

namespace APIBack.Automation.Services.Interface
{
    public interface IEstabelecimentoSelectionService
    {
        Task<IReadOnlyCollection<UsuarioEstabelecimentoDto>> ListarEstabelecimentosAsync(int userId);
        Task<DefinirEstabelecimentoAtivoResponse> DefinirEstabelecimentoAtivoAsync(int userId, Guid estabelecimentoId);
    }
}
