using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using APIBack.Payments.MercadoPago;

namespace APIBack.Payments.InfinitePay.Cliente
{
    /// <summary>Configuracao (secao "InfinitePay"). Sem segredo: a API Key da loja fica cifrada no banco.</summary>
    public sealed class InfinitePayOptions
    {
        public const string Secao = "InfinitePay";
        public string BaseUrl { get; set; } = "https://api.checkout.infinitepay.io/";
        public int TimeoutSegundos { get; set; } = 15;
        /// <summary>Tentativas totais por chamada (1 = sem retry). So 429, 5xx e falha de rede repetem, sempre com o MESMO corpo.</summary>
        public int MaxTentativas { get; set; } = 2;
        /// <summary>URL HTTPS publica do webhook do Zippy (nunca vem do navegador). Obrigatoria para criar link.</summary>
        public string? WebhookUrl { get; set; }
        /// <summary>Se preenchida, redirect_url so aceita estes hosts.</summary>
        public List<string> RedirectHostsPermitidos { get; set; } = new();
        /// <summary>Validade do link quando a loja tem API Key (valid_until). Sem chave o link nao expira.</summary>
        public int ValidadeMinutosPadrao { get; set; } = 24 * 60;
    }

    public static class InfinitePayCodigos
    {
        public const string Erro = "PAG_INFINITEPAY_ERRO";                              // 502
        public const string Indisponivel = "PAG_INFINITEPAY_INDISPONIVEL";             // 503
        public const string RespostaInconsistente = "PAG_INFINITEPAY_RESPOSTA_INCONSISTENTE"; // 502
        public const string ContaIncompativel = "PAG_INFINITEPAY_CONTA_INCOMPATIVEL";  // 422 (403 da API: handle e chave de contas diferentes)
        public const string CheckoutDesabilitado = "PAG_INFINITEPAY_CHECKOUT_DESABILITADO"; // 422 (external_checkout_not_enabled)
        public const string CriacaoEmAndamento = "PAG_INFINITEPAY_CRIACAO_EM_ANDAMENTO";    // 409
    }

    public sealed class InfinitePayItem
    {
        [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
        /// <summary>Centavos, inteiro (R$ 10,00 = 1000).</summary>
        [JsonPropertyName("price")] public long Price { get; set; }
        [JsonPropertyName("quantity")] public int Quantity { get; set; } = 1;
    }

    public sealed class InfinitePayCliente
    {
        [JsonPropertyName("name")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; set; }
        [JsonPropertyName("email")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Email { get; set; }
        [JsonPropertyName("phone_number")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PhoneNumber { get; set; }
    }

    /// <summary>Opcoes avancadas: a InfinitePay so as aceita com X-Api-Key. Campos nulos nao sao enviados.</summary>
    public sealed class InfinitePayOpcoes
    {
        [JsonPropertyName("assured_installments")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? AssuredInstallments { get; set; }
        [JsonPropertyName("require_delivery_address")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? RequireDeliveryAddress { get; set; }
        [JsonPropertyName("single_payment")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? SinglePayment { get; set; }
        [JsonPropertyName("valid_until")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ValidUntil { get; set; }
        [JsonPropertyName("payment_methods_enabled")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? PaymentMethodsEnabled { get; set; }
        [JsonPropertyName("discounts")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<string, decimal>? Discounts { get; set; }
    }

    /// <summary>Corpo de POST /links. Serializado uma vez e guardado: repeticao reenvia exatamente os mesmos bytes.</summary>
    public sealed class InfinitePayCriarLinkRequest
    {
        [JsonPropertyName("handle")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Handle { get; set; }
        [JsonPropertyName("items")] public List<InfinitePayItem> Items { get; set; } = new();
        [JsonPropertyName("order_nsu")] public string OrderNsu { get; set; } = string.Empty;
        [JsonPropertyName("redirect_url")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? RedirectUrl { get; set; }
        [JsonPropertyName("webhook_url")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? WebhookUrl { get; set; }
        [JsonPropertyName("customer")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public InfinitePayCliente? Customer { get; set; }
        [JsonPropertyName("options")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public InfinitePayOpcoes? Options { get; set; }
    }

    public sealed class InfinitePayLinkResposta
    {
        public string Url { get; init; } = string.Empty;
        /// <summary>SHA-256 do corpo da resposta (auditoria sem guardar dado desnecessario).</summary>
        public string RespostaHash { get; init; } = string.Empty;
    }

    /// <summary>
    /// Resposta de /payment_check (centavos). Divergencias voltam HTTP 200 com success/paid falsos. <c>Amount</c> e a cobranca original;
    /// <c>PaidAmount</c> pode ser maior (taxa repassada ao comprador) e NAO precisa igualar o amount.
    /// </summary>
    public sealed class InfinitePayPaymentCheck
    {
        public bool Success { get; init; }
        public bool Paid { get; init; }
        public long? Amount { get; init; }
        public long? PaidAmount { get; init; }
        public string? CaptureMethod { get; init; }

        public static InfinitePayPaymentCheck? Ler(string corpo)
        {
            try
            {
                using var doc = JsonDocument.Parse(corpo);
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object) return null;
                var capture = r.TryGetProperty("capture_method", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                return new InfinitePayPaymentCheck
                {
                    Success = r.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True,
                    Paid = r.TryGetProperty("paid", out var p) && p.ValueKind == JsonValueKind.True,
                    Amount = Inteiro(r, "amount"),
                    PaidAmount = Inteiro(r, "paid_amount"),
                    CaptureMethod = capture is InfinitePayRegras.MetodoPix or InfinitePayRegras.MetodoCartao ? capture : null
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static long? Inteiro(JsonElement r, string nome) =>
            r.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n >= 0 ? n : null;
    }

    /// <summary>Falha da API. Mensagem curta, nunca com chave, corpo ou URL.</summary>
    public sealed class InfinitePayApiException : Exception
    {
        public InfinitePayApiException(int statusHttp, string? erro, string mensagem, Exception? inner = null) : base(mensagem, inner)
        {
            StatusHttp = statusHttp;
            Erro = erro;
        }

        /// <summary>0 = sem resposta (rede/timeout): o resultado e desconhecido.</summary>
        public int StatusHttp { get; }
        public string? Erro { get; }
        public bool Indisponivel => StatusHttp == 0 || StatusHttp >= 500 || StatusHttp == 429;
        /// <summary>True quando a API respondeu e recusou de forma definitiva (nada foi criado).</summary>
        public bool RecusaDefinitiva => !Indisponivel;

        public PagamentoException ParaPagamento()
        {
            if (Indisponivel)
                return new PagamentoException(InfinitePayCodigos.Indisponivel, 503, "InfinitePay indisponivel no momento. Tente de novo.", inner: this);
            if (Erro == "external_checkout_not_enabled")
                return PagamentoException.Regra(InfinitePayCodigos.CheckoutDesabilitado, "O checkout integrado nao esta habilitado na conta InfinitePay da loja.");
            if (Erro == "resposta_sem_url")
                return new PagamentoException(InfinitePayCodigos.RespostaInconsistente, 502, "Resposta da InfinitePay inconsistente. Tente de novo.", inner: this);
            if (StatusHttp == 403)
                return PagamentoException.Regra(InfinitePayCodigos.ContaIncompativel, "A InfiniteTag e a API Key configuradas nao pertencem a mesma conta InfinitePay.");
            return new PagamentoException(InfinitePayCodigos.Erro, 502, "A InfinitePay recusou a operacao.",
                new Dictionary<string, object?> { ["infinitepayStatus"] = StatusHttp, ["infinitepayErro"] = Erro }, this);
        }
    }
}
