using System;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class WebhookDispatchService : IWebhookDispatchService
    {
        private readonly WebhookMessageQueue _queue;
        private readonly ILogger<WebhookDispatchService> _logger;

        public WebhookDispatchService(
            WebhookMessageQueue queue,
            ILogger<WebhookDispatchService> logger)
        {
            _queue = queue;
            _logger = logger;
        }

        public async Task EnqueueAsync(ConversationProcessingInput input, CancellationToken cancellationToken)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            var envelope = new WebhookProcessingEnvelope(input, DateTime.UtcNow);
            await _queue.EnqueueAsync(envelope, cancellationToken);

            _logger.LogDebug(
                "[Webhook] Mensagem {MensagemId} enfileirada para processamento",
                input.Mensagem?.Id ?? "sem-id");
        }
    }
}
