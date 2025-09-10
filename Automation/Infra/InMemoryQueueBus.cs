// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Collections.Concurrent;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;

namespace APIBack.Automation.Infra
{
    public class InMemoryQueueBus : IQueueBus
    {
        private readonly ConcurrentQueue<Message> _entrada = new();
        private readonly ConcurrentQueue<Message> _saida = new();
        private readonly ConcurrentQueue<string> _alertas = new();
        private readonly ConcurrentQueue<string> _deadletter = new();

        public Task PublicarEntradaAsync(Message mensagem)
        {
            _entrada.Enqueue(mensagem);
            return Task.CompletedTask;
        }

        public Task PublicarSaidaAsync(Message mensagem)
        {
            _saida.Enqueue(mensagem);
            return Task.CompletedTask;
        }

        public Task PublicarAlertaAsync(string mensagem)
        {
            _alertas.Enqueue(mensagem);
            return Task.CompletedTask;
        }

        public Task PublicarDeadLetterAsync(string payload)
        {
            _deadletter.Enqueue(payload);
            return Task.CompletedTask;
        }

        // Expostos para Health (sem incluir na interface por ora)
        public int QuantidadeEntrada => _entrada.Count;
        public int QuantidadeSaida => _saida.Count;
        public int QuantidadeAlertas => _alertas.Count;
        public int QuantidadeDeadLetter => _deadletter.Count;
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
