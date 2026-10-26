using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace APIBack.Payments.MercadoPago
{
    // Formato da API do MP (snake_case). So os campos que o nucleo usa; nada de dado de cartao alem de bandeira e final.
    // Pix e cartao do checkout usam a Orders API: MpOrder (resposta) vira o MpPagamento interno em MpOrder.ParaPagamento.

    /// <summary>POST v1/orders. Valores monetarios vao como string "0.00".</summary>
    public sealed class MpCriarOrderRequest
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "online";
        [JsonPropertyName("processing_mode")] public string ProcessingMode { get; set; } = "automatic";
        [JsonPropertyName("total_amount")] public string TotalAmount { get; set; } = string.Empty;
        [JsonPropertyName("external_reference")] public string ExternalReference { get; set; } = string.Empty;
        [JsonPropertyName("payer")] public MpPayer Payer { get; set; } = new();
        [JsonPropertyName("transactions")] public MpOrderTransacoesRequest Transactions { get; set; } = new();
        /// <summary>Comissao de marketplace. So vai quando o split estiver ligado (hoje: nunca).</summary>
        [JsonPropertyName("marketplace_fee")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MarketplaceFee { get; set; }

        [JsonIgnore] public MpOrderPagamentoRequest Pagamento => Transactions.Payments[0];

        public static string Moeda(decimal valor) => valor.ToString("0.00", CultureInfo.InvariantCulture);
    }

    public sealed class MpOrderTransacoesRequest
    {
        [JsonPropertyName("payments")] public List<MpOrderPagamentoRequest> Payments { get; set; } = new() { new MpOrderPagamentoRequest() };
    }

    public sealed class MpOrderPagamentoRequest
    {
        [JsonPropertyName("amount")] public string Amount { get; set; } = string.Empty;
        [JsonPropertyName("payment_method")] public MpOrderMetodoRequest PaymentMethod { get; set; } = new();
        /// <summary>Duracao ISO 8601 (PT30M). So Pix; o MP aceita de 30 minutos a 30 dias.</summary>
        [JsonPropertyName("expiration_time")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ExpirationTime { get; set; }
    }

    public sealed class MpOrderMetodoRequest
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "pix";
        /// <summary>bank_transfer (Pix), credit_card ou debit_card.</summary>
        [JsonPropertyName("type")] public string Type { get; set; } = "bank_transfer";
        [JsonPropertyName("token")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Token { get; set; }
        [JsonPropertyName("installments")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Installments { get; set; }
        [JsonPropertyName("statement_descriptor")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? StatementDescriptor { get; set; }
    }

    public sealed class MpPayer
    {
        [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
        [JsonPropertyName("first_name")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? FirstName { get; set; }
        [JsonPropertyName("identification")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public MpIdentification? Identification { get; set; }
    }

    public sealed class MpIdentification
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "CPF";
        [JsonPropertyName("number")] public string Number { get; set; } = string.Empty;
    }

    public sealed class MpPagamento
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        /// <summary>Id da order (ORD...) quando o pagamento nasceu na Orders API; null nos pagamentos da API v1 (mensalidades).</summary>
        [JsonPropertyName("order_id")] public string? OrderId { get; set; }
        [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
        [JsonPropertyName("status_detail")] public string? StatusDetail { get; set; }
        [JsonPropertyName("collector_id")] public long? CollectorId { get; set; }
        [JsonPropertyName("transaction_amount")] public decimal TransactionAmount { get; set; }
        [JsonPropertyName("transaction_amount_refunded")] public decimal? TransactionAmountRefunded { get; set; }
        [JsonPropertyName("currency_id")] public string? CurrencyId { get; set; }
        [JsonPropertyName("live_mode")] public bool LiveMode { get; set; }
        [JsonPropertyName("external_reference")] public string? ExternalReference { get; set; }
        [JsonPropertyName("payment_method_id")] public string? PaymentMethodId { get; set; }
        [JsonPropertyName("payment_type_id")] public string? PaymentTypeId { get; set; }
        [JsonPropertyName("installments")] public int? Installments { get; set; }
        [JsonPropertyName("date_created")] public DateTimeOffset? DateCreated { get; set; }
        [JsonPropertyName("date_approved")] public DateTimeOffset? DateApproved { get; set; }
        [JsonPropertyName("date_last_updated")] public DateTimeOffset? DateLastUpdated { get; set; }
        [JsonPropertyName("date_of_expiration")] public DateTimeOffset? DateOfExpiration { get; set; }
        [JsonPropertyName("point_of_interaction")] public MpPointOfInteraction? PointOfInteraction { get; set; }
        [JsonPropertyName("card")] public MpCard? Card { get; set; }
        [JsonPropertyName("refunds")] public List<MpRefund>? Refunds { get; set; }
        [JsonPropertyName("metadata")] public Dictionary<string, object?>? Metadata { get; set; }

        public MpTransactionData? TransactionData => PointOfInteraction?.TransactionData;
    }

    public sealed class MpPointOfInteraction
    {
        [JsonPropertyName("transaction_data")] public MpTransactionData? TransactionData { get; set; }
    }

    public sealed class MpTransactionData
    {
        [JsonPropertyName("qr_code")] public string? QrCode { get; set; }
        [JsonPropertyName("qr_code_base64")] public string? QrCodeBase64 { get; set; }
        [JsonPropertyName("ticket_url")] public string? TicketUrl { get; set; }
    }

    public sealed class MpCard
    {
        [JsonPropertyName("last_four_digits")] public string? LastFourDigits { get; set; }
    }

    public sealed class MpRefund
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("date_created")] public DateTimeOffset? DateCreated { get; set; }
    }

    /// <summary>Resposta da Orders API. O nucleo trabalha com um pagamento por order.</summary>
    public sealed class MpOrder
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
        [JsonPropertyName("status_detail")] public string? StatusDetail { get; set; }
        [JsonPropertyName("external_reference")] public string? ExternalReference { get; set; }
        [JsonPropertyName("total_amount")][JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public decimal? TotalAmount { get; set; }
        [JsonPropertyName("user_id")][JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public long? UserId { get; set; }
        [JsonPropertyName("live_mode")] public bool? LiveMode { get; set; }
        [JsonPropertyName("created_date")] public DateTimeOffset? CreatedDate { get; set; }
        [JsonPropertyName("last_updated_date")] public DateTimeOffset? LastUpdatedDate { get; set; }
        [JsonPropertyName("transactions")] public MpOrderTransacoes? Transactions { get; set; }

        [JsonIgnore] public MpOrderPagamento? Pagamento => Transactions?.Payments?.FirstOrDefault();

        /// <summary>Id numerico estavel do pagamento: o reference_id (id do pagamento no MP); sem ele, um hash do id textual.</summary>
        public static long IdNumerico(string? referenceId, string? idTextual)
        {
            if (long.TryParse(referenceId, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0) return n;
            var h = 14695981039346656037UL;
            foreach (var c in idTextual ?? string.Empty) h = (h ^ c) * 1099511628211UL;
            return (long)(h & 0x7FFFFFFFFFFFFFFFUL) | 1;
        }

        /// <summary>
        /// Retrato interno do pagamento da order. A resposta da Orders API nem sempre traz live_mode: <paramref name="liveModePadrao"/>
        /// vem do ambiente da credencial usada na chamada.
        /// </summary>
        public MpPagamento ParaPagamento(bool liveModePadrao)
        {
            var pag = Pagamento ?? throw new MercadoPagoApiException(502, "order_sem_pagamento", "Order do Mercado Pago sem pagamento.");
            var proprio = !string.IsNullOrEmpty(pag.Status);
            var status = proprio ? pag.Status : Status;
            var detalhe = proprio ? pag.StatusDetail : StatusDetail;
            var metodo = pag.PaymentMethod;
            var estornos = (Transactions?.Refunds ?? new List<MpOrderEstorno>()).Select(r => new MpRefund
            {
                Id = IdNumerico(r.ReferenceId, r.Id),
                Amount = r.Amount ?? 0m,
                Status = r.Status is null or "processed" or "approved" ? "approved" : r.Status,
                DateCreated = r.DateCreated
            }).ToList();
            return new MpPagamento
            {
                Id = IdNumerico(pag.ReferenceId, pag.Id),
                OrderId = Id,
                Status = status,
                StatusDetail = PagamentoStatusRules.DetalheCompativel(status, detalhe),
                CollectorId = UserId,
                TransactionAmount = pag.Amount ?? TotalAmount ?? 0m,
                TransactionAmountRefunded = estornos.Where(e => e.Status == "approved").Sum(e => e.Amount),
                LiveMode = LiveMode ?? liveModePadrao,
                ExternalReference = ExternalReference,
                PaymentMethodId = metodo?.Id,
                PaymentTypeId = metodo?.Type,
                Installments = metodo?.Installments,
                DateCreated = CreatedDate,
                DateApproved = status == "processed" ? LastUpdatedDate : null,
                DateLastUpdated = LastUpdatedDate,
                PointOfInteraction = string.IsNullOrEmpty(metodo?.QrCode) && string.IsNullOrEmpty(metodo?.TicketUrl) ? null : new MpPointOfInteraction
                {
                    TransactionData = new MpTransactionData { QrCode = metodo!.QrCode, QrCodeBase64 = metodo.QrCodeBase64, TicketUrl = metodo.TicketUrl }
                },
                Refunds = estornos
            };
        }
    }

    public sealed class MpOrderTransacoes
    {
        [JsonPropertyName("payments")] public List<MpOrderPagamento>? Payments { get; set; }
        [JsonPropertyName("refunds")] public List<MpOrderEstorno>? Refunds { get; set; }
    }

    public sealed class MpOrderPagamento
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("reference_id")] public string? ReferenceId { get; set; }
        [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
        [JsonPropertyName("status_detail")] public string? StatusDetail { get; set; }
        [JsonPropertyName("amount")][JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public decimal? Amount { get; set; }
        [JsonPropertyName("payment_method")] public MpOrderMetodo? PaymentMethod { get; set; }
    }

    public sealed class MpOrderMetodo
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("installments")] public int? Installments { get; set; }
        [JsonPropertyName("qr_code")] public string? QrCode { get; set; }
        [JsonPropertyName("qr_code_base64")] public string? QrCodeBase64 { get; set; }
        [JsonPropertyName("ticket_url")] public string? TicketUrl { get; set; }
    }

    public sealed class MpOrderEstorno
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("reference_id")] public string? ReferenceId { get; set; }
        [JsonPropertyName("transaction_id")] public string? TransactionId { get; set; }
        [JsonPropertyName("amount")][JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public decimal? Amount { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("date_created")] public DateTimeOffset? DateCreated { get; set; }
    }

    public sealed class MpChargeback
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("payments")] public List<long>? Payments { get; set; }
        [JsonPropertyName("amount")] public decimal? Amount { get; set; }
        [JsonPropertyName("coverage_applied")] public bool? CoverageApplied { get; set; }
        [JsonPropertyName("documentation_status")] public string? DocumentationStatus { get; set; }
        [JsonPropertyName("date_documentation_deadline")] public DateTimeOffset? DateDocumentationDeadline { get; set; }
    }

    /// <summary>Resposta do /oauth/token. Os tokens saem daqui direto para o protector; nunca para log ou resposta.</summary>
    public sealed class MpOAuthToken
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = string.Empty;
        [JsonPropertyName("public_key")] public string? PublicKey { get; set; }
        [JsonPropertyName("user_id")] public long UserId { get; set; }
        [JsonPropertyName("expires_in")] public long ExpiresIn { get; set; }
        [JsonPropertyName("scope")] public string? Scope { get; set; }
        [JsonPropertyName("live_mode")] public bool LiveMode { get; set; }

        public override string ToString() => $"MpOAuthToken(user={UserId}, expires_in={ExpiresIn})";
    }

    /// <summary>GET /users/me. So identificacao e situacao; nada de documento ou endereco.</summary>
    public sealed class MpUsuario
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("site_id")] public string? SiteId { get; set; }
        [JsonPropertyName("status")] public MpUsuarioStatus? Status { get; set; }
    }

    public sealed class MpUsuarioStatus
    {
        [JsonPropertyName("site_status")] public string? SiteStatus { get; set; }
    }

    public sealed class MpMeioPagamento
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("payment_type_id")] public string? PaymentTypeId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    /// <summary>Corpo do aviso de webhook (o estado real e sempre buscado no MP).</summary>
    public sealed class MpWebhookNotificacao
    {
        [JsonPropertyName("id")] public object? Id { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("topic")] public string? Topic { get; set; }
        [JsonPropertyName("action")] public string? Action { get; set; }
        [JsonPropertyName("live_mode")] public bool? LiveMode { get; set; }
        [JsonPropertyName("user_id")] public object? UserId { get; set; }
        [JsonPropertyName("data")] public MpWebhookData? Data { get; set; }
    }

    public sealed class MpWebhookData
    {
        [JsonPropertyName("id")] public object? Id { get; set; }
    }
}
