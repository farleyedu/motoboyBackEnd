using System.Threading;
using System.Threading.Tasks;
using APIBack.Automation.Services;

namespace APIBack.Automation.Interfaces
{
    public interface IWebhookDispatchService
    {
        Task EnqueueAsync(ConversationProcessingInput input, CancellationToken cancellationToken);
    }
}
