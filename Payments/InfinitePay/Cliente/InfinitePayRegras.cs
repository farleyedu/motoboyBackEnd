using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using APIBack.Payments.MercadoPago;

namespace APIBack.Payments.InfinitePay.Cliente
{
    /// <summary>Limites documentados da InfinitePay (checkout integrado). Nada aqui chama a rede.</summary>
    public static class InfinitePayRegras
    {
        public const long PrecoMinimoCentavos = 100;           // R$ 1,00
        public const int ParcelasAssumidasMax = 12;
        public const string MetodoCartao = "credit_card";
        public const string MetodoPix = "pix";

        public static long Centavos(decimal valor)
        {
            if (decimal.Round(valor, 2) != valor) throw PagamentoException.Regra(PagamentoCodigos.ValorInvalido, "Valor com mais de 2 casas decimais.");
            return decimal.ToInt64(valor * 100m);
        }

        public static void ValidarItens(IReadOnlyList<InfinitePayItem> itens, long totalEsperadoCentavos)
        {
            if (itens == null || itens.Count == 0) throw PagamentoException.Invalida("Informe ao menos um item.", "itens");
            long soma = 0;
            foreach (var i in itens)
            {
                if (string.IsNullOrWhiteSpace(i.Description)) throw PagamentoException.Invalida("Descricao do item obrigatoria.", "itens");
                if (i.Quantity < 1) throw PagamentoException.Invalida("Quantidade do item invalida.", "itens");
                if (i.Price < PrecoMinimoCentavos)
                    throw PagamentoException.Regra(PagamentoCodigos.ValorInvalido, "Cada item precisa custar ao menos R$ 1,00 na InfinitePay.");
                soma = checked(soma + i.Price * i.Quantity);
            }
            if (soma != totalEsperadoCentavos)
                throw PagamentoException.Regra(PagamentoCodigos.ValorInvalido, "A soma dos itens nao confere com o total da cobranca.");
        }

        public static void ValidarOpcoes(InfinitePayOpcoes? o, DateTime agoraUtc)
        {
            if (o == null) return;
            if (o.AssuredInstallments is < 0 or > ParcelasAssumidasMax)
                throw PagamentoException.Invalida("Parcelas assumidas pela loja devem ficar entre 0 e 12.", "parcelas");
            if (o.ValidUntil != null)
            {
                if (!DateTimeOffset.TryParse(o.ValidUntil, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ate))
                    throw PagamentoException.Invalida("Validade invalida.", "validade");
                if (ate.UtcDateTime <= agoraUtc || ate.UtcDateTime > agoraUtc.AddYears(1))
                    throw PagamentoException.Invalida("A validade precisa estar no futuro e em ate um ano.", "validade");
            }
            if (o.PaymentMethodsEnabled != null &&
                (o.PaymentMethodsEnabled.Count == 0 || o.PaymentMethodsEnabled.Any(m => m is not (MetodoCartao or MetodoPix))))
                throw PagamentoException.Invalida("Metodos aceitos: credit_card e pix.", "metodos");
            if (o.Discounts != null)
            {
                foreach (var par in o.Discounts)
                {
                    if (par.Key is not (MetodoPix or MetodoCartao) || par.Value <= 0 || par.Value >= 100)
                        throw PagamentoException.Invalida("Desconto deve ser maior que 0 e menor que 100 para pix ou credit_card.", "desconto");
                }
            }
        }

        /// <summary>Opcoes avancadas sem API Key nunca sao aceitas em silencio.</summary>
        public static bool TemOpcoes(InfinitePayOpcoes? o) =>
            o != null && (o.AssuredInstallments != null || o.RequireDeliveryAddress != null || o.SinglePayment != null || o.ValidUntil != null ||
                          o.PaymentMethodsEnabled != null || o.Discounts != null);

        public static void ValidarUrlHttps(string? url, string campo, IReadOnlyCollection<string>? hostsPermitidos = null)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
                throw PagamentoException.Invalida("URL precisa ser HTTPS valida.", campo);
            if (hostsPermitidos is { Count: > 0 } && !hostsPermitidos.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
                throw PagamentoException.Invalida("Host da URL nao permitido.", campo);
        }

        /// <summary>Metodos efetivos do checkout: cartao e sempre aceito; Pix fica ligado salvo opcao explicita da loja (nunca "so Pix").</summary>
        public static string[] MetodosEfetivos(InfinitePayOpcoes? o) =>
            o?.PaymentMethodsEnabled != null && !o.PaymentMethodsEnabled.Contains(MetodoPix)
                ? new[] { MetodoCartao }
                : new[] { MetodoPix, MetodoCartao };
    }
}
