namespace APIBack.DTOs.Cardapio;

public sealed class CardapioMotoboyAttentionDto
{
    public List<Guid> Produtos { get; set; } = new();
    public List<Guid> Adicionais { get; set; } = new();
}

public sealed class CardapioMotoboyAttentionRequest
{
    public bool? Atencao { get; set; }
}
