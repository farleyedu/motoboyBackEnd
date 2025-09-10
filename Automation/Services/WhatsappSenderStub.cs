// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class WhatsappSenderStub : IWhatsappSender
    {
        private readonly ILogger<WhatsappSenderStub> _logger;
        public WhatsappSenderStub(ILogger<WhatsappSenderStub> logger)
        {
            _logger = logger;
        }

        public Task<bool> EnviarTextoAsync(string idWa, string mensagem)
        {
            _logger.LogInformation("[Automation] Enviando texto WA (stub) para {WaId}: {Message}", idWa, mensagem);
            return Task.FromResult(true);
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
