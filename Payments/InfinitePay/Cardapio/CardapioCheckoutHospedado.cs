using System;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Model.Cardapio;
using APIBack.Payments.InfinitePay.Checkout;
using APIBack.Payments.InfinitePay.Cliente;
using APIBack.Payments.MercadoPago;
using APIBack.Payments.Provedores;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Payments.InfinitePay.Cardapio
{
    /// <summary>O que o cliente do cardapio recebe do checkout hospedado. Sem QR nem copia-e-cola: Pix ou cartao se escolhe na pagina da InfinitePay.</summary>
    public sealed class PublicCheckoutHospedadoDto
    {
        public string Tipo { get; set; } = "hosted_checkout";
        public string Provedor { get; set; } = ProvedoresPagamento.InfinitePay;
        public string Status { get; set; } = string.Empty;
        public int Versao { get; set; }
        public decimal Valor { get; set; }
        /// <summary>Link HTTPS da InfinitePay. So vai enquanto a intencao esta aberta.</summary>
        public string? UrlCheckout { get; set; }
        public string[] MetodosEfetivos { get; set; } = Array.Empty<string>();
        public DateTime? ExpiraEm { get; set; }
    }

    public interface ICardapioCheckoutHospedadoService
    {
        /// <summary>Ajusta a configuracao publica (Mercado Pago) pela preferencia da loja: capacidade do link hospedado, sem Brick/Pix MP quando o provedor e outro.</summary>
        Task<BrickConfiguracao> AjustarConfiguracaoAsync(Guid loja, BrickConfiguracao mp, CancellationToken ct = default);
        /// <summary>true se a loja ainda pode abrir uma NOVA cobranca nativa do Mercado Pago (pix ou cartao). Cobranca ja aberta continua no provedor antigo.</summary>
        Task<bool> PermiteNovoMercadoPagoAsync(Guid loja, string metodo, CancellationToken ct = default);
        /// <summary>Exige InfinitePay escolhida e pronta para abrir um link NOVO.</summary>
        Task ExigirParaNovoLinkAsync(Guid loja, CancellationToken ct = default);
        Task<PublicCheckoutHospedadoDto> CriarAsync(CardapioPedidoPublico pedido, string chave, CancellationToken ct = default);
        Task<PublicCheckoutHospedadoDto> ObterAsync(Guid intentId, Guid loja, CancellationToken ct = default);
        /// <summary>Link vencido: encerra a intencao antiga (sem efeito de dinheiro) para que outra possa nascer.</summary>
        Task ExpirarAsync(Guid intentId, Guid loja, CancellationToken ct = default);
    }

    public sealed class CardapioCheckoutHospedadoService : ICardapioCheckoutHospedadoService
    {
        private readonly NpgsqlDataSource _db;
        private readonly IPagamentoProvedorPreferenciaResolver _preferencias;
        private readonly IInfinitePayCheckoutService _checkout;
        private readonly IPagamentoSincronizador _sync;
        private readonly IConfiguration _config;

        public CardapioCheckoutHospedadoService(NpgsqlDataSource db, IPagamentoProvedorPreferenciaResolver preferencias, IInfinitePayCheckoutService checkout,
            IPagamentoSincronizador sync, IConfiguration config)
        {
            _db = db;
            _preferencias = preferencias;
            _checkout = checkout;
            _sync = sync;
            _config = config;
        }

        public async Task<BrickConfiguracao> AjustarConfiguracaoAsync(Guid loja, BrickConfiguracao mp, CancellationToken ct = default)
        {
            var checkout = await LerAsync(loja, FluxosPagamento.CheckoutOnline, ct);
            if (checkout == null) return mp; // preferencias indisponiveis (migration pendente): vale o legado Mercado Pago
            var pix = await LerAsync(loja, FluxosPagamento.PixExclusivo, ct);

            if (!checkout.Habilitado)
            {
                // Desabilitado de proposito: sem fallback silencioso para outro provedor.
                mp.PixAtivo = false; mp.CartaoAtivo = false; mp.PublicKey = null; mp.CheckoutHospedado = false; mp.ProvedorCheckout = null;
                return mp;
            }
            mp.ProvedorCheckout = checkout.Provedor;
            if (checkout.Provedor != ProvedoresPagamento.InfinitePay) return mp;

            // InfinitePay no checkout: sem Payment Brick. O Pix nativo (QR/copia-e-cola) so continua se a loja o escolheu de forma explicita no Mercado Pago.
            mp.CartaoAtivo = false;
            mp.PublicKey = null;
            mp.PixAtivo = mp.PixAtivo && pix is { Habilitado: true, Provedor: ProvedoresPagamento.MercadoPago };
            mp.CheckoutHospedado = await InfinitePayProntaAsync(loja, ct);
            return mp;
        }

        public async Task<bool> PermiteNovoMercadoPagoAsync(Guid loja, string metodo, CancellationToken ct = default)
        {
            var checkout = await LerAsync(loja, FluxosPagamento.CheckoutOnline, ct);
            if (checkout == null) return true;
            if (!checkout.Habilitado) return false;
            if (checkout.Provedor == ProvedoresPagamento.MercadoPago) return true;
            if (metodo != "pix") return false; // cartao nativo nao existe com InfinitePay
            var pix = await LerAsync(loja, FluxosPagamento.PixExclusivo, ct);
            return pix is { Habilitado: true, Provedor: ProvedoresPagamento.MercadoPago };
        }

        public async Task ExigirParaNovoLinkAsync(Guid loja, CancellationToken ct = default)
        {
            var checkout = await LerAsync(loja, FluxosPagamento.CheckoutOnline, ct);
            if (checkout is not { Habilitado: true, Provedor: ProvedoresPagamento.InfinitePay } || !await InfinitePayProntaAsync(loja, ct))
                throw PagamentoException.Regra(ProvedorPagamentoCodigos.ProvedorNaoConfigurado, "A loja nao esta recebendo pagamento por link agora. Escolha outra forma de pagamento.");
        }

        public async Task<PublicCheckoutHospedadoDto> CriarAsync(CardapioPedidoPublico pedido, string chave, CancellationToken ct = default)
        {
            // Valor, itens e descricao vem do pre-pedido no servidor. Um item so, com o total (frete, taxa e desconto ja dentro) em centavos.
            var centavos = InfinitePayRegras.Centavos(pedido.Total);
            var pedidoCheckout = new InfinitePayCheckoutRequest
            {
                IdEstabelecimento = pedido.IdEstabelecimento, PrePedidoId = pedido.Id, Valor = pedido.Total,
                Descricao = $"Pedido {pedido.Codigo}", IdempotencyKey = chave, RedirectUrl = RedirectUrl(pedido.IdEstabelecimento)
            };
            pedidoCheckout.Itens.Add(new InfinitePayItem { Description = $"Pedido {pedido.Codigo}", Price = centavos, Quantity = 1 });
            return Mapear(await _checkout.CriarAsync(pedidoCheckout, ct));
        }

        public async Task<PublicCheckoutHospedadoDto> ObterAsync(Guid intentId, Guid loja, CancellationToken ct = default) =>
            Mapear(await _checkout.ObterAsync(intentId, loja, ct));

        public async Task ExpirarAsync(Guid intentId, Guid loja, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            var intent = await PagamentoIntentSql.ObterAsync(cx, tx, intentId, true, ct);
            if (intent == null || intent.IdEstabelecimento != loja || intent.Provedor != ProvedoresPagamento.InfinitePay || !StatusIntent.Aberto(intent.Status))
            {
                await tx.CommitAsync(ct);
                return;
            }
            await _sync.MarcarIntentAsync(cx, tx, intent, StatusIntent.Expirado, "sistema", null, ct);
            await tx.CommitAsync(ct);
        }

        private static PublicCheckoutHospedadoDto Mapear(InfinitePayCheckoutResultado r) => new()
        {
            Status = r.Status, Versao = r.Versao, Valor = r.Valor, MetodosEfetivos = r.MetodosEfetivos, ExpiraEm = r.ExpiraEm,
            UrlCheckout = StatusIntent.Aberto(r.Status) ? r.UrlCheckout : null
        };

        /// <summary>Volta ao cardapio da loja. O pedido e retomado pelo que o navegador guardou; nada na URL prova pagamento ou carrega o codigo do pedido.</summary>
        private string? RedirectUrl(Guid loja)
        {
            var baseUrl = _config["InfinitePay:RedirectBaseUrl"]?.Trim().TrimEnd('/');
            return string.IsNullOrWhiteSpace(baseUrl) || !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? null : $"{baseUrl}/cardapio/{loja}";
        }

        private async Task<bool> InfinitePayProntaAsync(Guid loja, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            try
            {
                return await cx.ExecuteScalarAsync<bool>(new CommandDefinition(@"
SELECT EXISTS (SELECT 1 FROM infinitepay_conta_loja c JOIN infinitepay_conta_versao v ON v.id_conta = c.id AND v.ativa
                WHERE c.id_estabelecimento = @Loja AND c.habilitada AND c.status IN ('configurado', 'recebimento_validado'))",
                    new { Loja = loja }, cancellationToken: ct));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
            {
                return false;
            }
        }

        private async Task<PreferenciaLeve?> LerAsync(Guid loja, string fluxo, CancellationToken ct)
        {
            try
            {
                var p = await _preferencias.ResolverAsync(loja, fluxo, ct);
                return new PreferenciaLeve(p.Habilitado, p.Provedor);
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
            {
                return null;
            }
        }

        private sealed record PreferenciaLeve(bool Habilitado, string? Provedor);
    }
}
