// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace APIBack.Automation.Interfaces
{
    public interface IEstabelecimentoRepository
    {
        Task<IReadOnlyCollection<string>> ObterModulosAtivosAsync(Guid idEstabelecimento);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================