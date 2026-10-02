using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Model.Cardapio;

namespace APIBack.Repository.Interface
{
    /// <summary>Conversa de WhatsApp de um telefone com a loja e quando a janela de 24h dela fecha.</summary>
    public sealed record ConversaPorTelefone(Guid ConversaId, DateTimeOffset? JanelaFim)
    {
        public bool JanelaAberta => JanelaFim.HasValue && JanelaFim.Value > DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Pre-pedido do cardapio web (cardapio_pedido_publico) na confirmacao pelo WhatsApp. Cada transicao e um
    /// UPDATE condicional ao status: quem chega depois (clique duplo, dois atendentes, mensagem repetida)
    /// nao altera nada e recebe false/null.
    /// </summary>
    public interface ICardapioPedidoWebRepository
    {
        Task<CardapioPedidoPublico?> ObterAsync(Guid id);
        Task<CardapioPedidoPublico?> ObterAsync(Guid estabelecimentoId, Guid id);

        /// <summary>Marca como expirado o que esperava codigo e passou da validade (libera o codigo no indice).</summary>
        Task<int> ExpirarCodigosVencidosAsync(Guid estabelecimentoId);

        /// <summary>
        /// Grava um codigo novo e a validade. Colisao = o codigo ja esta ativo na loja (tentar outro);
        /// Pedido nulo sem colisao = o pedido nao esta num status que aceite codigo.
        /// </summary>
        Task<(CardapioPedidoPublico? Pedido, bool Colisao)> DefinirCodigoAsync(Guid id, string codigo, TimeSpan validade);

        /// <summary>Janela aberta: pedido recem-criado vai direto para aguardando_aceite.</summary>
        Task<bool> MarcarAguardandoAceiteAsync(Guid id, string telefoneContato, Guid conversaId);

        /// <summary>Codigo ativo da loja chegou por mensagem: aguardando_aceite, com o numero de quem mandou.</summary>
        Task<CardapioPedidoPublico?> ConfirmarPorCodigoAsync(Guid estabelecimentoId, string codigo, string telefoneContato, Guid conversaId);

        Task<IReadOnlyList<CardapioPedidoPublico>> ListarAguardandoAceiteAsync(Guid estabelecimentoId, int limite);

        Task<bool> MarcarAceitoAsync(Guid estabelecimentoId, Guid id, int? pedidoId);
        Task<bool> MarcarRecusadoAsync(Guid estabelecimentoId, Guid id, string? motivo);

        /// <summary>Conversa mais recente do telefone (qualquer das variantes com/sem nono digito) na loja.</summary>
        Task<ConversaPorTelefone?> ObterConversaPorTelefoneAsync(Guid estabelecimentoId, IReadOnlyList<string> variantes);
    }
}
