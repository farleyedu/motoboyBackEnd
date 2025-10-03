// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class MessageService : IMessageService
    {
        private readonly IMessageRepository _repo;
        private readonly ILogger<MessageService> _logger;
        private readonly IConfiguration _configuration;


        public MessageService(IMessageRepository repo, ILogger<MessageService> logger, 
                              IConfiguration configuration)
        {
            _repo = repo;
            _logger = logger;
            _configuration = configuration;

        }

        public async Task<Message?> AdicionarMensagemAsync(Message mensagem, string? phoneNumberId, string? idWa)
        {
            // Usa IdProvedor se informado; senão usa IdMensagemWa
            var idProv = !string.IsNullOrWhiteSpace(mensagem.IdProvedor) ? mensagem.IdProvedor : mensagem.IdMensagemWa;

            var ambiente = _configuration.GetValue<string>("ASPNETCORE_ENVIRONMENT");
            var isDev = string.Equals(ambiente, "Development", StringComparison.OrdinalIgnoreCase);


            if (!string.IsNullOrWhiteSpace(idProv))
            {
                var existe = await _repo.ExistsByProviderIdAsync(idProv);
                if (existe)
                {

                    if (isDev)
                    {
                        _logger.LogWarning(
                            "DEV: Duplicata detectada IdMensagemWa={WaMessageId}, processamento continuará para testes.",idProv,idWa);
                    }
                    else
                    {
                        _logger.LogInformation("Ignorando mensagem duplicada (id_provedor={IdProv}) para conversa {Conversa}", idProv, mensagem.IdConversa);
                        return null;
                    }
                }
            }

            await _repo.AddMessageAsync(mensagem, phoneNumberId, idWa ?? string.Empty);
            return mensagem;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
