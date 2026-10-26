using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Model.Cardapio;

namespace APIBack.Repository.Interface
{
    /// <summary>Conversa de WhatsApp de um telefone com a loja: quando a janela de 24h fecha e a ultima mensagem recebida dele.</summary>
    public sealed record ConversaPorTelefone(Guid ConversaId, DateTimeOffset? JanelaFim, DateTimeOffset? UltimaEntrada)
    {
        public bool JanelaAberta => JanelaFim.HasValue && JanelaFim.Value > DateTimeOffset.UtcNow;

        /// <summary>Mensagem recebida dentro da janela dada (1h no acesso do cliente); datas futuras não autenticam.</summary>
        public bool FalouRecentemente(TimeSpan janela) => UltimaEntrada.HasValue && UltimaEntrada.Value <= DateTimeOffset.UtcNow && UltimaEntrada.Value > DateTimeOffset.UtcNow - janela;
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

        /// <summary>Troca o telefone somente enquanto o pedido aguarda codigo; invalida o codigo anterior.</summary>
        Task<bool> TrocarTelefoneAsync(Guid id, string telefoneCliente);

        /// <summary>
        /// Janela aberta: pedido recem-criado vai direto para aguardando_aceite (presencial ou online ja pago) ou para
        /// aguardando_pagamento (online sem pagamento confirmado).
        /// </summary>
        Task<bool> MarcarAguardandoAceiteAsync(Guid id, string telefoneContato, Guid conversaId);

        /// <summary>Codigo ativo da loja chegou por mensagem: aguardando_aceite (ou aguardando_pagamento), com o numero de quem mandou.</summary>
        Task<CardapioPedidoPublico?> ConfirmarPorCodigoAsync(Guid estabelecimentoId, string codigo, string telefoneContato, Guid conversaId);
        Task<bool> CodigoDeOutroTelefoneAsync(Guid estabelecimentoId, string codigo, string telefoneContato);

        /// <summary>O que o restaurante decide: confirmados (presencial) ou confirmados e pagos (online), e os aceites em andamento.</summary>
        Task<IReadOnlyList<CardapioPedidoPublico>> ListarAguardandoAceiteAsync(Guid estabelecimentoId, int limite);

        /// <summary>
        /// Reserva o aceite (aguardando_aceite -> aceitando) antes de criar o pedido real: a partir daqui recusa e cancelamento
        /// nao passam. Pedido online sem pagamento confirmado nunca e reservado. Repetir (clique duplo) devolve true.
        /// </summary>
        Task<bool> IniciarAceiteAsync(Guid estabelecimentoId, Guid id);
        /// <summary>A criacao do pedido real falhou: aceitando volta a aguardando_aceite.</summary>
        Task DesfazerAceiteAsync(Guid estabelecimentoId, Guid id);
        /// <summary>aceitando -> aceito, com o pedido real (entrega).</summary>
        Task<bool> MarcarAceitoAsync(Guid estabelecimentoId, Guid id, int? pedidoId);

        /// <summary>
        /// Recusa (aguardando_aceite -> recusado) e, se o pedido estava pago, grava o reembolso integral automatico na mesma transacao.
        /// </summary>
        Task<CardapioDecisao> RecusarAsync(Guid estabelecimentoId, Guid id, string? motivo);

        /// <summary>
        /// Cancela o pre-pedido a partir de um dos status dados e, se ja estava pago, grava o reembolso integral automatico na mesma
        /// transacao. Sem mudanca (status fora da lista) devolve Mudou = false com o status atual.
        /// </summary>
        Task<CardapioDecisao> CancelarAsync(Guid id, string motivoCancelamento, string motivoReembolso, IReadOnlyCollection<string> statusPermitidos);

        /// <summary>Conversa mais recente do telefone (qualquer das variantes com/sem nono digito) na loja.</summary>
        Task<ConversaPorTelefone?> ObterConversaPorTelefoneAsync(Guid estabelecimentoId, IReadOnlyList<string> variantes);

        // ---- Pagamento online -------------------------------------------------------------------------------

        /// <summary>Pre-pedido criado antes com a mesma chave do checkout (retentativa).</summary>
        Task<CardapioPedidoPublico?> ObterPorChaveAsync(Guid estabelecimentoId, string chaveIdempotencia);

        /// <summary>Grava o vinculo do pre-pedido com a intencao recem-criada (a mais recente; a paga nunca e trocada).</summary>
        Task VincularIntentAsync(Guid prePedidoId, Guid intentId);

        /// <summary>Intencoes do cardapio deste pre-pedido, da mais antiga para a mais nova.</summary>
        Task<IReadOnlyList<CardapioIntentResumo>> ListarIntentsAsync(Guid prePedidoId);

        /// <summary>Resumo do pagamento para a tela do cliente e do restaurante (intencao que importa + reembolso mais recente).</summary>
        Task<CardapioPagamentoResumo?> ObterResumoPagamentoAsync(Guid prePedidoId);

        Task<IReadOnlyList<CardapioReembolso>> ListarReembolsosAsync(Guid estabelecimentoId, Guid prePedidoId);
        Task<CardapioReembolso?> ObterReembolsoAsync(Guid estabelecimentoId, Guid reembolsoId);

        /// <summary>
        /// Pedido de reembolso da loja depois do aceite (aguardando_aprovacao). Trava o pre-pedido e confere que a soma dos
        /// reembolsos vivos mais este nao passa do pago; devolve null se passar.
        /// </summary>
        Task<CardapioReembolso?> SolicitarReembolsoAsync(Guid estabelecimentoId, Guid prePedidoId, string tipo, decimal? valor, string? justificativa,
            int usuarioId, string chave);

        /// <summary>aguardando_aprovacao -> reembolso_pendente (aprovar; integral cancela o pre-pedido) ou negado. False se ja decidido.</summary>
        Task<bool> DecidirReembolsoAsync(Guid estabelecimentoId, Guid reembolsoId, bool aprovar, int usuarioId, string? justificativa);

        /// <summary>Reserva ate <paramref name="limite"/> reembolsos pendentes vencidos (lease de alguns minutos; varias instancias nao pegam o mesmo).</summary>
        Task<IReadOnlyList<CardapioReembolso>> ReservarReembolsosPendentesAsync(int limite, Guid? somenteId = null);

        /// <summary>Resultado da chamada ao Mercado Pago para um reembolso.</summary>
        Task AtualizarReembolsoAsync(Guid reembolsoId, string status, Guid? estornoId, string? erro, TimeSpan? tentarDeNovoEm);

        /// <summary>Pedidos online pagos que nao confirmaram o numero ate o prazo (o dinheiro volta).</summary>
        Task<IReadOnlyList<Guid>> ListarPagosSemConfirmacaoAsync(TimeSpan prazo, int limite);

        /// <summary>Status do pedido real do delivery (pendente, atribuido, em_rota...), para a fase do cancelamento.</summary>
        Task<string?> ObterStatusPedidoRealAsync(Guid estabelecimentoId, int pedidoId);
    }

    /// <summary>Resultado de recusa/cancelamento. <see cref="ReembolsoId"/> != null quando o dinheiro vai voltar.</summary>
    public sealed record CardapioDecisao(bool Mudou, string Status, CardapioPedidoPublico? Pedido, Guid? ReembolsoId, decimal ValorReembolso);

    public sealed class CardapioIntentResumo
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = string.Empty;
        public string IdempotencyKey { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }

    public sealed class CardapioPagamentoResumo
    {
        public Guid? IntentId { get; set; }
        public string? StatusIntent { get; set; }
        public int Versao { get; set; }
        public decimal Valor { get; set; }
        public decimal ValorPago { get; set; }
        public decimal ValorEstornado { get; set; }
        public string? Metodo { get; set; }
        public string? StatusDetalhe { get; set; }
        public string? CartaoBandeira { get; set; }
        public string? CartaoFinal { get; set; }
        public DateTime? ExpiraEm { get; set; }
        public DateTime? PagoEm { get; set; }
        public string? StatusReembolso { get; set; }
        public decimal ValorReembolso { get; set; }
    }

    public sealed class CardapioReembolso
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public Guid PrePedidoId { get; set; }
        public Guid IdIntent { get; set; }
        public string Tipo { get; set; } = string.Empty;
        public string Motivo { get; set; } = string.Empty;
        public decimal Valor { get; set; }
        public string? Justificativa { get; set; }
        public string Status { get; set; } = string.Empty;
        public int? SolicitadoPor { get; set; }
        public int? DecididoPor { get; set; }
        public DateTimeOffset? DecididoEm { get; set; }
        public Guid? IdEstorno { get; set; }
        public string IdempotencyKey { get; set; } = string.Empty;
        public int Tentativas { get; set; }
        public string? UltimoErro { get; set; }
        public DateTimeOffset? ConcluidoEm { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}
