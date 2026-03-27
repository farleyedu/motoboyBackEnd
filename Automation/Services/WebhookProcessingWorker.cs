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
                    var conversationRepository = scope.ServiceProvider.GetRequiredService<IConversationRepository>();
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
                    var (resetIntercepted, resetDecision) = await contextInterceptor.TryHandleResetAsync(
                        idConversa,
                        processamento.TextoUsuario,
                        processamento.NumeroTelefoneExibicao);
                    if (resetIntercepted && resetDecision != null)
                    {
                        _logger.LogInformation("[Conversa={Conversa}] Mensagem de reset interceptada antes da validacao de controle", idConversa);
                        await iaResponseHandler.HandleAsync(resetDecision, processamento);
                        continue;
                    }

                    var controle = await conversationRepository.ObterControleConversaAsync(idConversa);
                    if (controle == null)
                    {
                        _logger.LogWarning(
                            "[Conversa={Conversa}] Pipeline automatico interrompido: controle da conversa nao encontrado",
                            idConversa);
                        continue;
                    }

                    if (!controle.CanBotReply)
                    {
                        _logger.LogInformation(
                            "[Conversa={Conversa}] Pipeline automatico interrompido: status={Status}, agente={AgenteId}",
                            idConversa,
                            controle.Status,
                            controle.AssignedAgentId);
                        continue;
                    }

                    var (intercepted, interceptedDecision) = await contextInterceptor.TryInterceptAsync(
                        idConversa,
                        processamento.TextoUsuario,
                        envelope.Input.DataMensagemUtc,
                        processamento.NumeroTelefoneExibicao);

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
