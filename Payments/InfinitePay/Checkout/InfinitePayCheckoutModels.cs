using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.InfinitePay.Cliente;

namespace APIBack.Payments.InfinitePay.Checkout
{
    /// <summary>Opcoes avancadas pedidas pelo chamador. Todas exigem API Key da loja: sem chave o servico recusa em vez de ignorar.</summary>
    public sealed class InfinitePayOpcoesSolicitadas
    {
        /// <summary>assured_installments 0..12 (parcelas cujas taxas a loja assume).</summary>
        public int? ParcelasAssumidas { get; set; }
        /// <summary>Percentual (0..100, exclusivo) de desconto no Pix. So com autorizacao da loja; nunca e padrao.</summary>
        public decimal? DescontoPixPercentual { get; set; }
        public bool? ExigirEnderecoEntrega { get; set; }
        /// <summary>Padrao true quando ha API Key.</summary>
        public bool? PagamentoUnico { get; set; }
        /// <summary>Padrao InfinitePay:ValidadeMinutosPadrao quando ha API Key; maximo 1 ano.</summary>
        public int? ValidadeMinutos { get; set; }
    }

    /// <summary>
    /// Pedido de checkout. Valor e itens vem do pedido VALIDADO no servidor (nunca do navegador); a soma dos itens (taxa de entrega e
    /// ajustes entram como itens) tem de bater com o total, em centavos.
    /// </summary>
    public sealed class InfinitePayCheckoutRequest
    {
        public Guid IdEstabelecimento { get; set; }
        /// <summary>cardapio (padrao) ou chat (cobranca do atendente por link). Define a origem da intencao e o order_nsu.</summary>
        public string Origem { get; set; } = APIBack.Payments.MercadoPago.OrigensPagamento.Cardapio;
        /// <summary>Cardapio: obrigatorio. Chat: vazio para cobranca avulsa.</summary>
        public Guid PrePedidoId { get; set; }
        public int? PedidoId { get; set; }
        public Guid? ConversaId { get; set; }
        public Guid? ClienteId { get; set; }
        public decimal Valor { get; set; }
        public string Descricao { get; set; } = string.Empty;
        public List<InfinitePayItem> Itens { get; set; } = new();
        /// <summary>Chave estavel do chamador (ex.: cardapio:{prePedidoId}:infinitepay). Mesma chave = mesma intencao e mesmo link.</summary>
        public string IdempotencyKey { get; set; } = string.Empty;
        /// <summary>HTTPS controlado pelo Zippy. Nunca prova de pagamento.</summary>
        public string? RedirectUrl { get; set; }
        public InfinitePayCliente? Cliente { get; set; }
        public InfinitePayOpcoesSolicitadas? Opcoes { get; set; }
        public int? CriadoPor { get; set; }
    }

    public sealed class InfinitePayLimitesDto
    {
        public long PrecoMinimoCentavos { get; set; } = InfinitePayRegras.PrecoMinimoCentavos;
        public int ParcelasAssumidasMax { get; set; } = InfinitePayRegras.ParcelasAssumidasMax;
        public int ValidadeMaximaDias { get; set; } = 365;
    }

    /// <summary>
    /// Retorno do checkout hospedado. NAO ha QR nem copia-e-cola: o cliente escolhe Pix ou cartao na pagina da InfinitePay.
    /// Status pago so chega por confirmacao do servidor (webhook + /payment_check), nunca pelo redirect.
    /// </summary>
    public sealed class InfinitePayCheckoutResultado
    {
        public string Tipo { get; set; } = "hosted_checkout";
        public Guid IntentId { get; set; }
        public string Status { get; set; } = string.Empty;
        public int Versao { get; set; }
        public decimal Valor { get; set; }
        public string[] MetodosEfetivos { get; set; } = Array.Empty<string>();
        public string UrlCheckout { get; set; } = string.Empty;
        public string OrderNsu { get; set; } = string.Empty;
        public DateTime? ExpiraEm { get; set; }
        public bool PagamentoUnico { get; set; }
        public bool Reaproveitado { get; set; }
        public InfinitePayLimitesDto Limites { get; set; } = new();
    }

    public interface IInfinitePayCheckoutService
    {
        /// <summary>Cria (ou devolve) o link da intencao. Idempotente por (loja, chave); nunca troca de conta recebedora.</summary>
        Task<InfinitePayCheckoutResultado> CriarAsync(InfinitePayCheckoutRequest request, CancellationToken ct = default);
        /// <summary>Recupera o link ja gravado, sem criar nada. 404 se a intencao nao tem link ou e de outra loja.</summary>
        Task<InfinitePayCheckoutResultado> ObterAsync(Guid intentId, Guid idEstabelecimento, CancellationToken ct = default);
    }
}
