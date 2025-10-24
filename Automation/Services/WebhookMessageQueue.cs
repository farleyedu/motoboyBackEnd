using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace APIBack.Automation.Services
{
    public class WebhookMessageQueue
    {
        private readonly Channel<WebhookProcessingEnvelope> _channel;

        public WebhookMessageQueue()
        {
            var options = new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false
            };

            _channel = Channel.CreateUnbounded<WebhookProcessingEnvelope>(options);
        }

        public ValueTask EnqueueAsync(WebhookProcessingEnvelope envelope, CancellationToken cancellationToken) =>
            _channel.Writer.WriteAsync(envelope, cancellationToken);

        public IAsyncEnumerable<WebhookProcessingEnvelope> ReadAllAsync(CancellationToken cancellationToken) =>
            _channel.Reader.ReadAllAsync(cancellationToken);
    }
}
