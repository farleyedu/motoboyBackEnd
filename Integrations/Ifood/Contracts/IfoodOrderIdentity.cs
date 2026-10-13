namespace APIBack.Integrations.Ifood.Contracts;

/// <summary>Vínculo oficial. Número visível nunca substitui o UUID nas operações.</summary>
public sealed record IfoodOrderIdentity
{
    public Guid EstabelecimentoId { get; }
    public Guid MerchantId { get; }
    public Guid ExternalOrderId { get; }
    public int PedidoId { get; }
    public string? DisplayNumber { get; }

    public IfoodOrderIdentity(Guid estabelecimentoId, Guid merchantId, Guid externalOrderId,
        int pedidoId, string? displayNumber)
    {
        if (estabelecimentoId == Guid.Empty) throw new ArgumentException("Estabelecimento obrigatório.", nameof(estabelecimentoId));
        if (merchantId == Guid.Empty) throw new ArgumentException("Merchant obrigatório.", nameof(merchantId));
        if (externalOrderId == Guid.Empty) throw new ArgumentException("UUID externo obrigatório.", nameof(externalOrderId));
        if (pedidoId <= 0) throw new ArgumentOutOfRangeException(nameof(pedidoId));
        EstabelecimentoId = estabelecimentoId;
        MerchantId = merchantId;
        ExternalOrderId = externalOrderId;
        PedidoId = pedidoId;
        DisplayNumber = string.IsNullOrWhiteSpace(displayNumber) ? null : displayNumber.Trim();
    }

    public string DisplayLabel => DisplayNumber is null ? "iFood · número indisponível" : $"iFood #{DisplayNumber}";
}
