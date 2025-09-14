// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Threading.Tasks;
using APIBack.Automation.Models;

namespace APIBack.Automation.Interfaces
{
    /// <summary>
    /// Interface para repositório de mapeamento de WhatsApp Business API phone numbers
    /// </summary>
    public interface IWabaPhoneRepository
    {
        /// <summary>
        /// Obtém o ID do estabelecimento baseado no phone_number_id do WhatsApp
        /// </summary>
        /// <param name="phoneNumberId">ID do número de telefone do WhatsApp Business API</param>
        /// <returns>ID do estabelecimento ou null se não encontrado</returns>
        Task<Guid?> ObterIdEstabelecimentoPorPhoneNumberIdAsync(string phoneNumberId);

        /// <summary>
        /// Obtém o mapeamento completo por phone_number_id
        /// </summary>
        /// <param name="phoneNumberId">ID do número de telefone do WhatsApp Business API</param>
        /// <returns>Objeto WabaPhone ou null se não encontrado</returns>
        Task<WabaPhone?> ObterPorPhoneNumberIdAsync(string phoneNumberId);

        /// <summary>
        /// Cria ou atualiza um mapeamento phone_number_id -> estabelecimento
        /// </summary>
        /// <param name="wabaPhone">Dados do mapeamento</param>
        /// <returns>True se operação foi bem-sucedida</returns>
        Task<bool> InserirOuAtualizarAsync(WabaPhone? wabaPhone);

        /// <summary>
        /// Verifica se existe um mapeamento ativo para o phone_number_id
        /// </summary>
        /// <param name="phoneNumberId">ID do número de telefone do WhatsApp Business API</param>
        /// <returns>True se existe e está ativo</returns>
        Task<bool> ExisteAtivoAsync(string phoneNumberId);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
