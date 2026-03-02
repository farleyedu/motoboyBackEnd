// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Models;

namespace APIBack.Automation.Interfaces
{
    public interface IEstabelecimentoRepository
    {
        Task<IReadOnlyCollection<string>> ObterModulosAtivosAsync(Guid idEstabelecimento);
        Task<IReadOnlyCollection<EstabelecimentoWhatsappAtivoDto>> ListarEstabelecimentosComWhatsappAtivoAsync(Guid? excluirEstabelecimentoId = null);
        Task<string?> ObterNomeFantasiaAsync(Guid idEstabelecimento);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
