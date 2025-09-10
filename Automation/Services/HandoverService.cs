// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class HandoverService
    {
        private readonly IConversationRepository _repositorio;
        private readonly IAlertSender _alertas;
        private readonly ILogger<HandoverService> _logger;

        public HandoverService(IConversationRepository repo, IAlertSender alerts, ILogger<HandoverService> logger)
        {
            _repositorio = repo;
            _alertas = alerts;
            _logger = logger;
        }

        public async Task DefinirHumanoAsync(Guid idConversa, string? agenteDesignado)
        {
            await _repositorio.DefinirModoAsync(idConversa, ModoConversa.Humano, agenteDesignado);
            var alerta = $"[Automation] Handover para humano. Conversa={idConversa}, Agente={agenteDesignado ?? "n/a"}";
            _logger.LogInformation(alerta);
            await _alertas.EnviarAlertaAsync(alerta);
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
