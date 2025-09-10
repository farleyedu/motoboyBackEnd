// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class AlertSenderTelegramStub : IAlertSender
    {
        private readonly ILogger<AlertSenderTelegramStub> _logger;
        public AlertSenderTelegramStub(ILogger<AlertSenderTelegramStub> logger)
        {
            _logger = logger;
        }

        public Task EnviarAlertaAsync(string mensagem)
        {
            _logger.LogInformation("[Automation] Alerta Telegram (stub): {Message}", mensagem);
            return Task.CompletedTask;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
