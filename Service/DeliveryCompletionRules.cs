using APIBack.DTOs.Delivery;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace APIBack.Service;

public static class DeliveryCompletionRules
{
    public static bool IsPaid(string? value) => value?.Trim().ToLowerInvariant() is "pago" or "paid" or "aprovado" or "approved" or "pago_online" or "settled";
    public static void Validate(DeliveryCompletionRequest request, decimal? total, bool requiresPayment)
    {
        if (request.OperationId == Guid.Empty || request.ExpectedPedidoId <= 0 || request.ExpectedVersion < 0 || request.Codigo?.Length > 32 || request.Payments == null || request.Payments.Count > 2 || request.Payments.Any(p => p==null))
            throw new DeliveryDomainException(422, "COMPLETION_INVALID", "Confira os dados desta conclusão.");
        if (!requiresPayment)
        {
            if (request.Payments.Count != 0) throw new DeliveryDomainException(409, "PAYMENT_ALREADY_PAID", "Este pedido já está pago. Não cobre novamente; revise a entrega.");
            return;
        }
        if (!total.HasValue || total <= 0 || decimal.Round(total.Value, 2) != total.Value)
            throw new DeliveryDomainException(422, "PAYMENT_TOTAL_INVALID", "O valor do pedido precisa ser conferido pela loja.");
        // Limitar antes da soma também evita overflow de decimal em corpos inválidos.
        if (request.Payments.Any(p => p.Amount > 1_000_000 || p.Amount < -1_000_000))
            throw new DeliveryDomainException(422, "PAYMENT_NOT_CONFIRMED", "Confira o valor de cada parte antes de concluir.");
        if (request.Payments.Count is < 1 or > 2 || request.Payments.Sum(p => p.Amount) != total.Value)
            throw new DeliveryDomainException(422, "PAYMENT_TOTAL_MISMATCH", "As partes precisam somar exatamente o total do pedido.");
        foreach (var part in request.Payments)
        {
            if (part.Method is not ("dinheiro" or "pix" or "debito" or "credito") || part.Amount <= 0 || part.Amount > 1_000_000 || decimal.Round(part.Amount, 2) != part.Amount || !part.ReceivedConfirmed)
                throw new DeliveryDomainException(422, "PAYMENT_NOT_CONFIRMED", "Confira o recebimento de cada parte antes de concluir.");
            if (part.Method == "dinheiro" && (!part.CashReceived.HasValue || part.CashReceived < part.Amount || part.CashReceived > 1_000_000 || decimal.Round(part.CashReceived.Value, 2) != part.CashReceived.Value))
                throw new DeliveryDomainException(422, "CASH_INSUFFICIENT", "Confira o dinheiro recebido e o troco de cada parte.");
            if (part.Method != "dinheiro" && part.CashReceived.HasValue)
                throw new DeliveryDomainException(422, "PAYMENT_INVALID", "Valor em dinheiro só se aplica à parte em dinheiro.");
        }
    }
    // Nunca persiste o código do cliente no recibo ou nos logs.
    public static string Hash(DeliveryCompletionRequest request)
    {
        // Mantém o hash das conclusões antigas para que um reenviou pós-atualização
        // encontre o recibo original, sem registrar entrega/recebimento novamente.
        object payload = request.Checklist == null
            ? new { request.OperationId, request.ExpectedPedidoId, request.ExpectedVersion, request.Codigo, request.ProofId, request.Payments }
            : request;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));
    }
}
