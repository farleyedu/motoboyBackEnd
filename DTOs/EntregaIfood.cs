public class ConfirmarEntregaRequest
{
    public string Localizador { get; set; }
    public string CodigoCliente { get; set; }
}

public class IfoodLocalizadorResponse
{
    public string orderId { get; set; }
    public string customerName { get; set; }
    public string shortId { get; set; }
    public string deliveryMethod { get; set; }
    public string storeType { get; set; }
}
