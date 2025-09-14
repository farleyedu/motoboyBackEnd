// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    // Stub simples de serviço de IA para respostas automáticas
    public class AssistantServiceStub : IAssistantService
    {
        private readonly ILogger<AssistantServiceStub> _logger;
        public AssistantServiceStub(ILogger<AssistantServiceStub> logger)
        {
            _logger = logger;
        }

        public Task<string> GerarRespostaAsync(string textoUsuario, Guid idConversa, object? contexto = null)
        {
            _logger.LogInformation("[Automation] IA stub acionada para conversa {Id}", idConversa);
            var resposta = string.IsNullOrWhiteSpace(textoUsuario)
                ? "Poderia repetir? Não consegui ler sua mensagem."
                : $"Você disse: '{textoUsuario}'. Como posso ajudar?";
            return Task.FromResult(resposta);
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

