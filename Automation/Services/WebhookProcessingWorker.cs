using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class WebhookProcessingWorker : BackgroundService
    {
        private readonly WebhookMessageQueue _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<WebhookProcessingWorker> _logger;

        public WebhookProcessingWorker(
            WebhookMessageQueue queue,
            IServiceScopeFactory scopeFactory,
            ILogger<WebhookProcessingWorker> logger)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await foreach (var envelope in _queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();

                    var conversationProcessor = scope.ServiceProvider.GetRequiredService<ConversationProcessor>();
                    var contextInterceptor = scope.ServiceProvider.GetRequiredService<ContextInterceptorService>();
                    var iaResponseHandler = scope.ServiceProvider.GetRequiredService<IAResponseHandler>();
                    var assistant = scope.ServiceProvider.GetService<IAssistantService>();

                    var processamento = await conversationProcessor.ProcessAsync(envelope.Input);
                    if (processamento.ShouldIgnore)
                    {
                        _logger.LogInformation("[WebhookWorker] Mensagem ignorada (id={MensagemId})", envelope.Input.Mensagem?.Id);
                        continue;
                    }

                    var idConversa = processamento.IdConversa ?? Guid.Empty;

                    var (intercepted, interceptedDecision) = await contextInterceptor.TryInterceptAsync(
                        idConversa,
                        processamento.TextoUsuario,
                        envelope.Input.DataMensagemUtc);

                    if (intercepted && interceptedDecision != null)
                    {
                        _logger.LogInformation("[Conversa={Conversa}] Mensagem interceptada por contexto ativo", idConversa);
                        await iaResponseHandler.HandleAsync(interceptedDecision, processamento);
                        continue;
                    }

                    AssistantDecision decision;
                    var stopwatch = Stopwatch.StartNew();

                    if (assistant != null)
                    {
                        decision = await assistant.GerarDecisaoComHistoricoAsync(
                            idConversa,
                            processamento.TextoUsuario,
                            processamento.Historico,
                            processamento.Contexto);
                    }
                    else
                    {
                        decision = new AssistantDecision(
                            processamento.TextoUsuario,
                            "none",
                            null,
                            false,
                            null);
                    }

                    stopwatch.Stop();
                    _logger.LogInformation("[Conversa={Conversa}] Latencia IA: {Latency} ms", idConversa, stopwatch.ElapsedMilliseconds);

                    await iaResponseHandler.HandleAsync(decision, processamento);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[WebhookWorker] Erro ao processar mensagem {MensagemId}", envelope.Input.Mensagem?.Id);
                }
            }
        }
    }
}
