// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using APIBack.Automation.Dtos;
using APIBack.Automation.Infra;
using APIBack.Automation.Interfaces;

namespace APIBack.Automation.Services
{
    public class AutomationHealthService
    {
        private readonly IQueueBus _queue;
        public AutomationHealthService(IQueueBus queue)
        {
            _queue = queue;
        }

        public HealthResponse ObterSaude()
        {
            var inmem = _queue as InMemoryQueueBus;
            return new HealthResponse
            {
                WhatsappCloudApiOk = true, // stub
                TamanhoFilaEntrada = inmem?.QuantidadeEntrada ?? 0,
                TamanhoFilaSaida = inmem?.QuantidadeSaida ?? 0,
                TamanhoFilaAlertas = inmem?.QuantidadeAlertas ?? 0,
                TamanhoFilaDeadLetter = inmem?.QuantidadeDeadLetter ?? 0,
            };
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
