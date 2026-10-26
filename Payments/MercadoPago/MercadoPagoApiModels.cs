using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace APIBack.Payments.MercadoPago
{
    // Formato da API v1 do MP (snake_case). So os campos que o nucleo usa; nada de dado de cartao alem de bandeira e final.

    public sealed class MpCriarPagamentoRequest
    {
        [JsonPropertyName("transaction_amount")] public decimal TransactionAmount { get; set; }
        [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
        [JsonPropertyName("payment_method_id")] public string PaymentMethodId { get; set; } = "pix";
        [JsonPropertyName("token")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Token { get; set; }
        [JsonPropertyName("installments")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Installments { get; set; }
        [JsonPropertyName("issuer_id")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? IssuerId { get; set; }
        [JsonPropertyName("payer")] public MpPayer Payer { get; set; } = new();
        [JsonPropertyName("external_reference")] public string ExternalReference { get; set; } = string.Empty;
        [JsonPropertyName("notification_url")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? NotificationUrl { get; set; }
        [JsonPropertyName("date_of_expiration")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? DateOfExpiration { get; set; }
        [JsonPropertyName("statement_descriptor")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? StatementDescriptor { get; set; }
        [JsonPropertyName("binary_mode")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? BinaryMode { get; set; }
        /// <summary>Comissao de marketplace. So vai quando o split estiver ligado (hoje: nunca).</summary>
        [JsonPropertyName("application_fee")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public decimal? ApplicationFee { get; set; }
        [JsonPropertyName("metadata")] public Dictionary<string, string> Metadata { get; set; } = new();
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

    public sealed class MpBuscaPagamentos
    {
        [JsonPropertyName("results")] public List<MpPagamento> Results { get; set; } = new();
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
