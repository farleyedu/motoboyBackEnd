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
        Task<string?> ObterPhoneNumberIdPorEstabelecimentoAsync(Guid idEstabelecimento);
        Task<string?> ObterDisplayPhonePorEstabelecimentoAsync(Guid idEstabelecimento);
        /// <summary>Telefone do numero que atende o servico na loja (ex.: "cardapio_web"). Nulo quando nenhum numero em uso atende o servico.</summary>
        Task<string?> ObterDisplayPhoneParaServicoAsync(Guid idEstabelecimento, string servicoCodigo);
        Task<string?> ObterAccessTokenPorPhoneNumberIdAsync(string phoneNumberId);
        Task<string?> ObterPhoneNumberIdPorDisplayPhoneAsync(string displayPhoneNumber);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
