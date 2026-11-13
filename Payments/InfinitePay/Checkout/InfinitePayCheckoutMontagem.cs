using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using APIBack.Payments.InfinitePay.Cliente;
using APIBack.Payments.MercadoPago;
using APIBack.Payments.Provedores;

namespace APIBack.Payments.InfinitePay.Checkout
{
    /// <summary>Monta e valida o corpo de POST /links sem tocar em banco ou rede (testavel).</summary>
    public static class InfinitePayCheckoutMontagem
    {
        /// <summary>Mensalidade e compra extra: quem recebe e a conta InfinitePay da ZIPPY, nunca a da loja.</summary>
        public static bool EhOrigemZippy(string origem) => origem is OrigensPagamento.Assinatura or OrigensPagamento.CompraExtra;

        public static void ValidarPedido(InfinitePayCheckoutRequest r)
        {
            if (r.IdEstabelecimento == Guid.Empty) throw PagamentoException.Invalida("Loja obrigatoria.", "estabelecimentoId");
            if (r.Origem is not (OrigensPagamento.Cardapio or OrigensPagamento.Chat or OrigensPagamento.Assinatura or OrigensPagamento.CompraExtra))
                throw PagamentoException.Invalida("Origem invalida para InfinitePay.", "origem");
            if (EhOrigemZippy(r.Origem) && r.OrigemRefId is null) throw PagamentoException.Invalida("Cobranca obrigatoria.", "origemRefId");
            if (r.Origem == OrigensPagamento.Cardapio && r.PrePedidoId == Guid.Empty) throw PagamentoException.Invalida("Pedido do cardapio obrigatorio.", "prePedidoId");
            if (r.Origem == OrigensPagamento.Chat && r.ConversaId is null) throw PagamentoException.Invalida("Conversa obrigatoria.", "conversaId");
            if (r.Valor <= 0 || r.Valor > 999_999.99m)
                throw PagamentoException.Regra(PagamentoCodigos.ValorInvalido, "Valor deve ser positivo e ate R$ 999.999,99.");
            var descricao = r.Descricao?.Trim() ?? string.Empty;
            if (descricao.Length is < 1 or > 140) throw PagamentoException.Invalida("Descricao obrigatoria (ate 140 caracteres).", "descricao");
            r.Descricao = descricao;
            if (string.IsNullOrWhiteSpace(r.IdempotencyKey) || r.IdempotencyKey.Length > 160)
                throw PagamentoException.Invalida("Chave de idempotencia obrigatoria (ate 160 caracteres).", "idempotencyKey");
            InfinitePayRegras.ValidarItens(r.Itens, InfinitePayRegras.Centavos(r.Valor));
        }

        /// <summary>
        /// Corpo do /links. <paramref name="temApiKey"/> libera o bloco options (single_payment, valid_until, methods...). Sem chave,
        /// qualquer opcao pedida e recusada (PAG_RECURSO_NAO_SUPORTADO) em vez de ignorada.
        /// </summary>
        public static (InfinitePayCriarLinkRequest Corpo, DateTime? ExpiraEm) Montar(InfinitePayCheckoutRequest r, string handle, string orderNsu,
            bool temApiKey, InfinitePayOptions config, DateTime agoraUtc)
        {
            InfinitePayRegras.ValidarUrlHttps(config.WebhookUrl, "webhookUrl");
            if (!string.IsNullOrWhiteSpace(r.RedirectUrl)) InfinitePayRegras.ValidarUrlHttps(r.RedirectUrl, "redirectUrl", config.RedirectHostsPermitidos);

            var corpo = new InfinitePayCriarLinkRequest
            {
                Handle = handle,
                Items = r.Itens.Select(i => new InfinitePayItem { Description = i.Description.Trim(), Price = i.Price, Quantity = i.Quantity }).ToList(),
                OrderNsu = orderNsu,
                RedirectUrl = string.IsNullOrWhiteSpace(r.RedirectUrl) ? null : r.RedirectUrl,
                WebhookUrl = config.WebhookUrl,
                Customer = TemCliente(r.Cliente) ? r.Cliente : null
            };

            var pedido = r.Opcoes;
            var pediuAlgo = pedido != null && (pedido.ParcelasAssumidas != null || pedido.DescontoPixPercentual != null ||
                                               pedido.ExigirEnderecoEntrega != null || pedido.PagamentoUnico != null || pedido.ValidadeMinutos != null);
            if (!temApiKey)
            {
                if (pediuAlgo)
                    throw PagamentoException.Regra(ProvedorPagamentoCodigos.NaoSuportado,
                        "Opcoes avancadas do checkout InfinitePay exigem a API Key da loja.");
                return (corpo, null);
            }

            var minutos = pedido?.ValidadeMinutos ?? config.ValidadeMinutosPadrao;
            if (minutos < 1) throw PagamentoException.Invalida("Validade invalida.", "validade");
            var expira = agoraUtc.AddMinutes(minutos);
            var opcoes = new InfinitePayOpcoes
            {
                SinglePayment = pedido?.PagamentoUnico ?? true,
                ValidUntil = expira.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture),
                PaymentMethodsEnabled = new List<string> { InfinitePayRegras.MetodoCartao, InfinitePayRegras.MetodoPix },
                AssuredInstallments = pedido?.ParcelasAssumidas,
                RequireDeliveryAddress = pedido?.ExigirEnderecoEntrega,
                Discounts = pedido?.DescontoPixPercentual is { } pct ? new Dictionary<string, decimal> { [InfinitePayRegras.MetodoPix] = pct } : null
            };
            InfinitePayRegras.ValidarOpcoes(opcoes, agoraUtc);
            corpo.Options = opcoes;
            return (corpo, expira);
        }

        private static bool TemCliente(InfinitePayCliente? c) =>
            c != null && (!string.IsNullOrWhiteSpace(c.Name) || !string.IsNullOrWhiteSpace(c.Email) || !string.IsNullOrWhiteSpace(c.PhoneNumber));
    }
}
