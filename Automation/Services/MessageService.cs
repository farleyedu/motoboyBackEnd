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
        private readonly APIBack.Atendimento.IChatRealtimePublisher _tempoReal;

        public MessageService(IMessageRepository repo, ILogger<MessageService> logger,
                              IConfiguration configuration, APIBack.Atendimento.IChatRealtimePublisher tempoReal)
        {
            _repo = repo;
            _logger = logger;
            _configuration = configuration;
            _tempoReal = tempoReal;
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
            await _tempoReal.MensagemCriadaAsync(mensagem);
            return mensagem;
        }

        public async Task AtualizarStatusAsync(Guid idMensagem, string status, string? codigoErro = null, string? mensagemErro = null)
        {
            await _repo.AtualizarStatusAsync(idMensagem, status, codigoErro, mensagemErro);
            var conversa = await _repo.ObterConversaDaMensagemAsync(idMensagem);
            if (conversa.HasValue)
            {
                await _tempoReal.StatusDaMensagemAsync(conversa.Value, idMensagem, null, MessageStatusMapper.NormalizeForDatabase(status, DirecaoMensagem.Saida), mensagemErro);
            }
        }

        public async Task<bool> AtualizarStatusPorProvedorAsync(string idProvedor, string status, string? codigoErro = null, string? mensagemErro = null)
        {
            var atualizada = await _repo.AtualizarStatusPorProvedorAsync(idProvedor, status, codigoErro, mensagemErro);
            if (atualizada)
            {
                var conversa = await _repo.ObterConversaPorProvedorAsync(idProvedor);
                if (conversa.HasValue)
                {
                    await _tempoReal.StatusDaMensagemAsync(conversa.Value, null, idProvedor, MessageStatusMapper.NormalizeForDatabase(status, DirecaoMensagem.Saida), mensagemErro);
                }
            }

            return atualizada;
        }

        public Task VincularProvedorAsync(Guid idMensagem, string idProvedor) => _repo.VincularProvedorAsync(idMensagem, idProvedor);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
