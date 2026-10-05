using System;
using System.Threading.Tasks;
using APIBack.Repository.Interface;
using Microsoft.Extensions.Logging;

namespace APIBack.Service
{
    /// <summary>Confirma com o cliente, mostrando os itens, um pedido que o atendente acabou de criar no painel.</summary>
    public interface IAtendenteConfirmacaoSender
    {
        /// <summary>
        /// Nunca lanca: a criacao do pedido ja aconteceu e nao pode ser desfeita por causa do aviso. Falha
        /// (ex.: numero sem janela de 24h aberta no WhatsApp) fica so registrada no historico do pedido.
        /// </summary>
        Task TrySendAsync(Guid estabelecimentoId, int pedidoId);
    }

    public sealed class AtendenteConfirmacaoService : IAtendenteConfirmacaoSender
    {
        private readonly IRastreioRepository _rastreio;
        private readonly IAtendimentoRepository _atendimento;
        private readonly ITrackingNoticeSender _sender;
        private readonly ILogger<AtendenteConfirmacaoService> _logger;

        public AtendenteConfirmacaoService(
            IRastreioRepository rastreio,
            IAtendimentoRepository atendimento,
            ITrackingNoticeSender sender,
            ILogger<AtendenteConfirmacaoService> logger)
        {
            _rastreio = rastreio;
            _atendimento = atendimento;
            _sender = sender;
            _logger = logger;
        }

        public async Task TrySendAsync(Guid estabelecimentoId, int pedidoId)
        {
            try
            {
                var settings = await _rastreio.GetSettingsAsync(estabelecimentoId);
                if (!settings.ConfirmacaoAtendenteEnabled) return;

                var reserved = await _rastreio.TryReserveAsync(pedidoId, NoticeTypes.ConfirmacaoAtendente);
                if (!reserved.HasValue) return; // ja enviada para este pedido

                var conversa = await _atendimento.AbrirConversaDoPedidoAsync(estabelecimentoId, pedidoId);
                if (conversa.ConversaId == null)
                {
                    await _rastreio.MarkAsync(reserved.Value, NoticeStatuses.Ignored, "sem_telefone_valido", null);
                    return;
                }
                if (!conversa.JanelaAberta)
                {
                    // Mesma limitacao de hoje nos avisos de rastreio: fora da janela so vale template aprovado
                    // da Meta, que ainda nao existe. Fica registrado no historico; o atendente pode reenviar
                    // depois que o cliente mandar uma mensagem (ou falar com ele por outro canal).
                    await _rastreio.MarkAsync(reserved.Value, NoticeStatuses.Failed, "fora_da_janela_sem_template", null);
                    return;
                }

                var values = await _atendimento.GetPedidoVariablesAsync(estabelecimentoId, pedidoId);
                var rendered = QuickReplyRenderer.Render(settings.ConfirmacaoAtendenteText, values);
                if (rendered.Pendentes.Count > 0)
                {
                    await _rastreio.MarkAsync(reserved.Value, NoticeStatuses.Failed,
                        "variavel_sem_valor:" + string.Join(",", rendered.Pendentes), null);
                    return;
                }

                var messageId = await _sender.SendAsync(conversa.ConversaId.Value, estabelecimentoId, rendered.Texto);
                await _rastreio.MarkAsync(reserved.Value, NoticeStatuses.Sent, null, messageId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Confirmacao do pedido {Pedido} (atendente) nao foi enviada.", pedidoId);
            }
        }
    }
}
