using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using APIBack.Payments.MercadoPago;

/// <summary>
/// Apoio dos MpFalso dos testes de banco: o estado fica num MpPagamento (statuses da API v1, como os testes ja mudam) e daqui sai
/// o JSON da Orders API (v1/orders) que o cliente real le. Id da order = "ORD" + id numerico do pagamento (o reference_id).
/// </summary>
internal static class MpOrdersFalso
{
    public static string OrderId(long pagamentoId) => $"ORD{pagamentoId}";

    public static bool TentaPagamento(string? orderId, out long pagamentoId)
    {
        pagamentoId = 0;
        return orderId != null && orderId.StartsWith("ORD", StringComparison.Ordinal) && long.TryParse(orderId[3..], out pagamentoId);
    }

    public static decimal Valor(JsonObject corpo) => decimal.Parse(corpo["total_amount"]!.GetValue<string>(), CultureInfo.InvariantCulture);
    public static string Referencia(JsonObject corpo) => corpo["external_reference"]!.GetValue<string>();
    public static JsonObject Metodo(JsonObject corpo) => corpo["transactions"]!["payments"]![0]!["payment_method"]!.AsObject();

    /// <summary>Valor do estorno parcial (transactions[0].amount); null = estorno total (corpo vazio).</summary>
    public static decimal? ValorEstorno(JsonObject? corpo) =>
        corpo?["transactions"]?[0]?["amount"]?.GetValue<string>() is { } s ? decimal.Parse(s, CultureInfo.InvariantCulture) : null;

    private static (string Status, string Detalhe) Traduz(MpPagamento p)
    {
        var estornado = p.Refunds?.Sum(r => r.Amount) ?? 0m;
        return p.Status switch
        {
            "pending" => ("action_required", "waiting_transfer"),
            "approved" when estornado > 0 && estornado < p.TransactionAmount => ("processed", "partially_refunded"),
            "approved" => ("processed", "accredited"),
            "cancelled" when p.StatusDetail == "expired" => ("canceled", "expired"),
            "cancelled" => ("canceled", "canceled"),
            "refunded" => ("refunded", "refunded"),
            "rejected" => ("failed", "rejected_by_issuer"),
            "in_process" => ("processing", "in_process"),
            var outro => (outro, p.StatusDetail ?? outro)
        };
    }

    public static JsonObject Order(MpPagamento p)
    {
        var (status, detalhe) = Traduz(p);
        var metodo = new JsonObject { ["id"] = p.PaymentMethodId, ["type"] = p.PaymentTypeId };
        if (p.TransactionData is { } t)
        {
            metodo["qr_code"] = t.QrCode;
            metodo["qr_code_base64"] = t.QrCodeBase64;
            metodo["ticket_url"] = t.TicketUrl;
        }
        var refunds = new JsonArray();
        foreach (var r in p.Refunds ?? new())
            refunds.Add(new JsonObject { ["id"] = $"REF{r.Id}", ["reference_id"] = r.Id.ToString(), ["transaction_id"] = $"PAY{p.Id}", ["amount"] = Moeda(r.Amount), ["status"] = "processed" });
        var valor = Moeda(p.TransactionAmount);
        return new JsonObject
        {
            ["id"] = OrderId(p.Id), ["type"] = "online", ["status"] = status, ["status_detail"] = detalhe, ["external_reference"] = p.ExternalReference,
            ["total_amount"] = valor, ["user_id"] = p.CollectorId?.ToString(), ["live_mode"] = p.LiveMode,
            ["created_date"] = p.DateCreated?.ToString("O"), ["last_updated_date"] = p.DateLastUpdated?.ToString("O"),
            ["transactions"] = new JsonObject
            {
                ["payments"] = new JsonArray(new JsonObject
                {
                    ["id"] = $"PAY{p.Id}", ["reference_id"] = p.Id.ToString(), ["status"] = status, ["status_detail"] = detalhe, ["amount"] = valor, ["payment_method"] = metodo
                }),
                ["refunds"] = refunds
            }
        };
    }

    public static string Moeda(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    public static HttpResponseMessage Resposta(MpPagamento p) =>
        new(HttpStatusCode.OK) { Content = new StringContent(Order(p).ToJsonString(), Encoding.UTF8, "application/json") };
}
