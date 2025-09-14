public class ConfirmarEntregaRequest
{
    public string Localizador { get; set; } = string.Empty;
    public string CodigoCliente { get; set; } = string.Empty;
}

public class IfoodLocalizadorResponse
{
    public string orderId { get; set; } = string.Empty;
    public string customerName { get; set; } = string.Empty;
    public string shortId { get; set; } = string.Empty;
    public string deliveryMethod { get; set; } = string.Empty;
    public string storeType { get; set; } = string.Empty;
}

