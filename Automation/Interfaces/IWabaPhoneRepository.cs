// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Threading.Tasks;
using APIBack.Automation.Models;

namespace APIBack.Automation.Interfaces
{
    /// <summary>
    /// Interface para repositorio de mapeamento de WhatsApp Business API phone numbers
    /// </summary>
    public interface IWabaPhoneRepository
    {
        Task<Guid?> ObterIdEstabelecimentoPorPhoneNumberIdAsync(string phoneNumberId);
        Task<Guid?> ObterIdEstabelecimentoPorDisplayPhoneAsync(string displayPhoneNumber);
        Task<bool> InserirOuAtualizarAsync(WabaPhone wabaPhone);
        Task<bool> ExisteAtivoAsync(string phoneNumberId);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
