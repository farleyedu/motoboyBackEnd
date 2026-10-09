namespace APIBack.DTOs.Delivery;

public sealed class DeliveryChecklistItem
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ParentName { get; set; }
    public int Quantity { get; set; }
    public string? ImageUrl { get; set; }
    public string? Note { get; set; }
    public bool Extra { get; set; }
}

public sealed class DeliveryChecklist
{
    public string Version { get; set; } = "";
    public bool DetailsUnavailable { get; set; }
    public List<DeliveryChecklistItem> Items { get; set; } = new();
}

public sealed class DeliveryChecklistConfirmation
{
    public int PedidoId { get; set; }
    public string Version { get; set; } = "";
    public List<string> ConfirmedKeys { get; set; } = new();
    public List<string> RecheckedExtraKeys { get; set; } = new();
}
