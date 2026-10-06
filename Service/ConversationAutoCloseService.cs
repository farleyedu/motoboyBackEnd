using System;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Services;
using APIBack.Repository.Interface;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace APIBack.Service
{
    /// <summary>Tipo reservado em pedido_notificacao (mesma tabela dos avisos de rastreio) so para marcar "ja tentei".</summary>
    public static class ConversationAutoCloseTypes
    {
        public const string EntregaConcluida = "atendimento_fechado_entrega";
    }

    /// <summary>
    /// Fecha sozinha a conversa do atendimento quando a entrega do pedido termina (pedido concluido): o atendente
    /// nao precisa lembrar de encerrar manualmente depois que o motoboy entrega. Vale para qualquer caminho de
    /// conclusao (atendente, motoboy real, simulador de motoboy ou de pedido) -- todos atualizam o mesmo
    /// status_pedido, que e o unico sinal que esta classe observa.
    ///
    /// Um disparo por pedido (reserva em pedido_notificacao, igual aos avisos de rastreio): se o atendente reabrir
    /// a conversa depois (ex.: o cliente reclamou de algo), o pedido ja concluido nao fecha ela de novo sozinho.
    /// </summary>
    public sealed class ConversationAutoCloseService
    {
        private readonly IAtendimentoRepository _atendimento;
        private readonly IRastreioRepository _rastreio;
        private readonly IConversationCloser _conversationManagement;
        private readonly ILogger<ConversationAutoCloseService> _logger;

        public ConversationAutoCloseService(
            IAtendimentoRepository atendimento,
            IRastreioRepository rastreio,
            IConversationCloser conversationManagement,
            ILogger<ConversationAutoCloseService> logger)
        {
            _atendimento = atendimento;
            _rastreio = rastreio;
            _conversationManagement = conversationManagement;
            _logger = logger;
        }

        /// <summary>Uma passada: fecha o que estiver pendente. Devolve quantas conversas fechou.</summary>
        public async Task<int> RunOnceAsync()
        {
            var candidates = await _atendimento.GetPedidosEntreguesComConversaAbertaAsync();
            var closed = 0;
            foreach (var candidate in candidates)
            {
                var reserved = await _rastreio.TryReserveAsync(candidate.PedidoId, ConversationAutoCloseTypes.EntregaConcluida);
                if (!reserved.HasValue) continue; // outro processo (ou uma passada anterior) ja cuidou deste pedido

                try
                {
                    await _conversationManagement.CloseAsync(
                        candidate.ConversaId,
                        candidate.EstabelecimentoId,
                        actorUserId: null,
                        actorName: "Sistema",
                        new CloseConversationRequest { Motivo = "Pedido entregue.", Tipo = "manual" });
                    await _rastreio.MarkAsync(reserved.Value, "enviada", null, null);
                    closed++;
                }
                catch (ConversationManagementException ex) when (ex.StatusCode == 409)
                {
                    // Corrida com o atendente (ele encerrou no mesmo instante, ou ja estava fechada): nao e falha.
                    await _rastreio.MarkAsync(reserved.Value, "ignorada", "conversa_ja_fechada", null);
                }
                catch (Exception ex)
                {
                    await _rastreio.MarkAsync(reserved.Value, "falhou", ex.Message, null);
                    _logger.LogWarning(ex, "Fechamento automatico da conversa do pedido {Pedido} falhou.", candidate.PedidoId);
                }
            }
            return closed;
        }
    }

    /// <summary>Roda o fechamento automatico a cada poucos segundos. So atua sobre conversas abertas de pedidos concluidos.</summary>
    public sealed class ConversationAutoCloseWorker : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<ConversationAutoCloseWorker> _logger;

        public ConversationAutoCloseWorker(IServiceScopeFactory scopes, ILogger<ConversationAutoCloseWorker> logger)
        {
            _scopes = scopes;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Espera a subida (migrations) antes da primeira passada.
            await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<ConversationAutoCloseService>().RunOnceAsync();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Passada de fechamento automatico de conversas falhou.");
                }

                try
                {
                    await Task.Delay(Interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
