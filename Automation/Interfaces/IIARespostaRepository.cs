// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Threading.Tasks;

namespace APIBack.Automation.Interfaces
{
    public interface IIARespostaRepository
    {
        Task RegistrarAsync(Guid? idRegra, Guid idConversa, string resposta);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

