// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class MessageService : IMessageService
    {
        private readonly IConversationRepository _repo;
        private readonly ILogger<MessageService> _logger;

        public MessageService(IConversationRepository repo, ILogger<MessageService> logger)
        {
            _repo = repo;
            _logger = logger;
        }

        public async Task<Message?> AdicionarMensagemAsync(Message mensagem, string? phoneNumberId, string? idWa)
        {
            // Usa IdProvedor se informado; senão usa IdMensagemWa
            var idProv = !string.IsNullOrWhiteSpace(mensagem.IdProvedor) ? mensagem.IdProvedor : mensagem.IdMensagemWa;

            if (!string.IsNullOrWhiteSpace(idProv))
            {
                var existe = await _repo.ExisteIdMensagemPorProvedorWaAsync(idProv);
                if (existe)
                {
                    _logger.LogInformation("Ignorando mensagem duplicada (id_provedor={IdProv}) para conversa {Conversa}", idProv, mensagem.IdConversa);
                    return null;
                }
            }

            await _repo.AcrescentarMensagemAsync(mensagem, phoneNumberId, idWa ?? string.Empty);
            return mensagem;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

