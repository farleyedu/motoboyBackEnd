using System;
using System.Collections.Generic;

namespace APIBack.DTOs.Cardapio
{
    /// <summary>Como o cliente confirma o pedido do cardapio web depois de finalizar.</summary>
    public class CardapioConfirmacaoDto
    {
        /// <summary>
        /// "mensagem_enviada": o WhatsApp do cliente recebeu a nossa mensagem e o pedido ja foi ao restaurante.
        /// "codigo": o cliente precisa mandar o codigo pelo WhatsApp da loja (<see cref="WhatsappUrl"/>).
        /// </summary>
        public string Modo { get; set; } = "codigo";
        public string? Codigo { get; set; }
        public DateTimeOffset? ExpiraEm { get; set; }
        public string? WhatsappUrl { get; set; }
    }

    /// <summary>Acompanhamento publico do pedido (a tela do cardapio consulta por aqui; o id e o segredo).</summary>
    public class CardapioPedidoPublicoStatusDto
    {
        public Guid Id { get; set; }
        public string Codigo { get; set; } = string.Empty;
        /// <summary>aguardando_codigo, aguardando_aceite, aceito, recusado ou expirado.</summary>
        public string Status { get; set; } = string.Empty;
        /// <summary>Preenchido so enquanto o codigo ainda vale.</summary>
        public CardapioConfirmacaoDto? Confirmacao { get; set; }
        public string? MotivoRecusa { get; set; }
        public int? NumeroPedido { get; set; }
        public string? TelefoneCheckout { get; set; }
        public string? TelefoneConfirmado { get; set; }
        public string TipoEntrega { get; set; } = "retirada";
        /// <summary>Pagamento online (Pix/cartao). Pagamento confirmado nao e pedido aceito: o aceite vem em <see cref="Status"/>.</summary>
        public CardapioPagamentoPublicoDto? Pagamento { get; set; }
        /// <summary>O cliente ainda pode desistir sozinho (antes do aceite). Pago: o dinheiro volta integralmente.</summary>
        public bool PodeCancelar { get; set; }
        public string? MotivoCancelamento { get; set; }
    }

    /// <summary>O pagamento online como o cliente ve. Nunca traz QR, copia e cola nem dado do cartao alem do final.</summary>
    public class CardapioPagamentoPublicoDto
    {
        /// <summary>pix ou cartao.</summary>
        public string? Metodo { get; set; }
        /// <summary>
        /// aguardando, processando, aprovado, recusado, expirado, cancelado, reembolso_pendente, reembolso_em_processamento,
        /// reembolsado, reembolsado_parcial (CardapioPagamentoRules.StatusParaCliente).
        /// </summary>
        public string Status { get; set; } = "aguardando";
        public int Versao { get; set; }
        public decimal Valor { get; set; }
        public decimal ValorEstornado { get; set; }
        /// <summary>Validade do Pix em aberto.</summary>
        public DateTime? ExpiraEm { get; set; }
        public DateTime? PagoEm { get; set; }
        /// <summary>status_detail do Mercado Pago no cartao recusado (cc_rejected_*): a tela escolhe a mensagem.</summary>
        public string? StatusDetalhe { get; set; }
        public string? CartaoBandeira { get; set; }
        public string? CartaoFinal { get; set; }
    }

    public class CancelarPedidoPublicoRequest
    {
        public string? Codigo { get; set; }
        public string? Motivo { get; set; }
    }

    /// <summary>Pagamento do pedido do cardapio no painel da loja: resumo, fase do cancelamento e reembolsos.</summary>
    public class CardapioPagamentoLojaDto
    {
        public bool Online { get; set; }
        public CardapioPagamentoPublicoDto? Pagamento { get; set; }
        /// <summary>antes_do_aceite, aceito_antes_do_preparo, em_preparo_ou_entrega, encerrado.</summary>
        public string Fase { get; set; } = string.Empty;
        public List<CardapioReembolsoDto> Reembolsos { get; set; } = new();
    }

    public class CardapioReembolsoDto
    {
        public Guid Id { get; set; }
        public string Tipo { get; set; } = string.Empty;
        public string Motivo { get; set; } = string.Empty;
        public decimal Valor { get; set; }
        public string? Justificativa { get; set; }
        public string Status { get; set; } = string.Empty;
        public int? SolicitadoPor { get; set; }
        public int? DecididoPor { get; set; }
        public DateTimeOffset? DecididoEm { get; set; }
        public string? UltimoErro { get; set; }
        public DateTimeOffset? ConcluidoEm { get; set; }
        public DateTimeOffset CriadoEm { get; set; }
    }

    public class SolicitarReembolsoCardapioRequest
    {
        /// <summary>Null = tudo o que ainda nao voltou. Antes do preparo so vale o integral.</summary>
        public decimal? Valor { get; set; }
        public string? Justificativa { get; set; }
        public string? IdempotencyKey { get; set; }
    }

    public class DecidirReembolsoCardapioRequest
    {
        public string? Justificativa { get; set; }
    }

    /// <summary>Endereco guardado no pre-pedido (com a coordenada achada no servidor).</summary>
    public class CardapioEnderecoArmazenado
    {
        public string? Logradouro { get; set; }
        public string? Numero { get; set; }
        public string? Complemento { get; set; }
        public string? Bairro { get; set; }
        public string? Cidade { get; set; }
        public string? Uf { get; set; }
        public string? Cep { get; set; }
        public string? Referencia { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        /// <summary>Como a coordenada foi obtida. Hoje so "exata" e aceita (numero achado pelo geocodificador).</summary>
        public string? Precisao { get; set; }
    }

    // ---- Fila do atendente ---------------------------------------------------------------------------

    public class CardapioPedidoAguardandoItemDto
    {
        public int Quantidade { get; set; }
        public string Nome { get; set; } = string.Empty;
        public List<string> Adicionais { get; set; } = new();
        public string? Observacao { get; set; }
    }

    /// <summary>Pedido confirmado pelo cliente que espera o restaurante aceitar ou recusar.</summary>
    public class CardapioPedidoAguardandoDto
    {
        public Guid Id { get; set; }
        public string Codigo { get; set; } = string.Empty;
        /// <summary>Quando o cliente confirmou: o painel mostra "ha X min" a partir daqui.</summary>
        public DateTimeOffset ConfirmadoEm { get; set; }
        public string NomeCliente { get; set; } = string.Empty;
        public string? Telefone { get; set; }
        public string? TelefoneCheckout { get; set; }
        public string? TelefoneConfirmado { get; set; }
        /// <summary>entrega ou retirada.</summary>
        public string TipoEntrega { get; set; } = "retirada";
        public string? Endereco { get; set; }
        public List<CardapioPedidoAguardandoItemDto> Itens { get; set; } = new();
        public string? Observacoes { get; set; }
        public string? FormaPagamento { get; set; }
        public decimal Total { get; set; }
        /// <summary>Pago online (Pix/cartao) e confirmado pelo Mercado Pago: nada a cobrar na entrega.</summary>
        public bool PagoOnline { get; set; }
        /// <summary>aceitando = outro atendente ja esta aceitando este pedido.</summary>
        public string Status { get; set; } = "aguardando_aceite";
    }

    public class RecusarCardapioPedidoRequest
    {
        public string? Motivo { get; set; }
    }

    public class TrocarTelefonePedidoPublicoRequest
    {
        public string? Telefone { get; set; }
    }

    public class AceitarCardapioPedidoResultDto
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = "aceito";
        /// <summary>Pedido criado no delivery (entrega); nulo na retirada.</summary>
        public int? PedidoId { get; set; }
        /// <summary>O que o cliente viu ao finalizar.</summary>
        public decimal TotalCliente { get; set; }
        /// <summary>O que o delivery calculou (taxa por zona); difere de <see cref="TotalCliente"/> quando a taxa mudou.</summary>
        public decimal? TotalPedido { get; set; }
        /// <summary>False quando a mensagem de aceito nao saiu (janela fechada ou falha do WhatsApp).</summary>
        public bool ClienteAvisado { get; set; }
        public List<string> Avisos { get; set; } = new();
    }
}
