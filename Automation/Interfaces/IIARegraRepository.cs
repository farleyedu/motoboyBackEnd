// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Models;

namespace APIBack.Automation.Interfaces
{
    public interface IIARegraRepository
    {
        Task<string?> ObterContextoAtivoAsync(Guid idEstabelecimento);
        Task<IEnumerable<IARegra>> ListaregrasAsync(Guid idEstabelecimento);
        Task<Guid> CriarAsync(Guid idEstabelecimento, string contexto);
        Task<bool> ExcluirAsync(Guid id);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

