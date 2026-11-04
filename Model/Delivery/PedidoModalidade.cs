namespace APIBack.Model.Delivery
{
    /// <summary>
    /// Como o pedido chega ao cliente: <c>retirada</c> (entregue na loja, nunca vai para motoboy) ou <c>delivery</c> (despachado por
    /// motoboy). Não é o canal de criação. Nulo = a origem não afirmou: nunca se deduz por nome, endereço ou ausência de motoboy.
    /// </summary>
    public static class PedidoModalidade
    {
        public const string Retirada = "retirada", Delivery = "delivery";

        /// <summary>Valor canônico, nulo se vazio; texto desconhecido é erro (nunca é descartado em silêncio).</summary>
        public static string? Normalize(string? valor)
        {
            if (string.IsNullOrWhiteSpace(valor)) return null;
            return valor.Trim().ToLowerInvariant() switch
            {
                "retirada" or "balcao" or "balcão" => Retirada,
                "delivery" or "entrega" => Delivery,
                _ => throw new APIBack.Service.DeliveryDomainException(422, "INVALID_MODALIDADE", "Modalidade invalida. Use: retirada ou delivery."),
            };
        }

        /// <summary>Tipo do pedido no iFood (DELIVERY, TAKEOUT, INDOOR) para a modalidade; INDOOR e desconhecido não são nenhum dos dois.</summary>
        public static string? DoTipoIfood(string? tipo) => tipo?.Trim().ToUpperInvariant() switch
        {
            "DELIVERY" => Delivery,
            "TAKEOUT" => Retirada,
            _ => null,
        };
    }
}
