using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.MercadoPago;
using Microsoft.Extensions.Logging;

namespace APIBack.Payments.Assinaturas
{
    public sealed class MpPreapprovalRecorrencia
    {
        [JsonPropertyName("frequency")] public int Frequency { get; set; } = 1;
        [JsonPropertyName("frequency_type")] public string FrequencyType { get; set; } = "months";
        [JsonPropertyName("start_date")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? StartDate { get; set; }
        [JsonPropertyName("transaction_amount")] public decimal TransactionAmount { get; set; }
        [JsonPropertyName("currency_id")] public string CurrencyId { get; set; } = "BRL";
    }

    public sealed class MpCriarPreapproval
    {
        [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
        [JsonPropertyName("external_reference")] public string ExternalReference { get; set; } = string.Empty;
        [JsonPropertyName("payer_email")] public string PayerEmail { get; set; } = string.Empty;
        [JsonPropertyName("card_token_id")] public string CardTokenId { get; set; } = string.Empty;
        [JsonPropertyName("auto_recurring")] public MpPreapprovalRecorrencia AutoRecurring { get; set; } = new();
        [JsonPropertyName("back_url")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? BackUrl { get; set; }
        [JsonPropertyName("status")] public string Status { get; set; } = "authorized";
    }

    public sealed class MpPreapproval
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("external_reference")] public string? ExternalReference { get; set; }
        [JsonPropertyName("collector_id")] public long? CollectorId { get; set; }
        [JsonPropertyName("next_payment_date")] public DateTimeOffset? NextPaymentDate { get; set; }
        [JsonPropertyName("auto_recurring")] public MpPreapprovalRecorrencia? AutoRecurring { get; set; }
        [JsonPropertyName("card_id")] public JsonElement? CardId { get; set; }
        [JsonPropertyName("payment_method_id")] public string? PaymentMethodId { get; set; }
    }

    public sealed class MpAuthorizedPaymentPagamento
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("status_detail")] public string? StatusDetail { get; set; }
    }

    /// <summary>Cobranca feita pelo proprio MP numa assinatura com cartao (subscription_authorized_payment).</summary>
    public sealed class MpAuthorizedPayment
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("preapproval_id")] public string? PreapprovalId { get; set; }
        /// <summary>scheduled | processed | recycling (o MP vai tentar de novo) | cancelled</summary>
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("transaction_amount")] public decimal TransactionAmount { get; set; }
        [JsonPropertyName("currency_id")] public string? CurrencyId { get; set; }
        [JsonPropertyName("debit_date")] public DateTimeOffset? DebitDate { get; set; }
        [JsonPropertyName("retry_attempt")] public int? RetryAttempt { get; set; }
        [JsonPropertyName("payment")] public MpAuthorizedPaymentPagamento? Payment { get; set; }
    }

    internal sealed class MpBuscaAuthorized
    {
        [JsonPropertyName("results")] public List<MpAuthorizedPayment> Results { get; set; } = new();
    }

    /// <summary>
    /// Assinaturas com cartao no MP (preapproval sem plano). Sempre na conta recebedora da Zippy. O MP cobra e repete as tentativas
    /// sozinho: a Zippy nunca recobra o mesmo ciclo pelo cartao. Mesmo HttpClient do nucleo; token so no header.
    /// </summary>
    public interface IMercadoPagoPreapprovalClient
    {
        Task<MpPreapproval> CriarAsync(CredencialRecebedora zippy, MpCriarPreapproval corpo, string idempotencyKey, CancellationToken ct = default);
        Task<MpPreapproval> ObterAsync(CredencialRecebedora zippy, string id, CancellationToken ct = default);
        Task<MpPreapproval> CancelarAsync(CredencialRecebedora zippy, string id, CancellationToken ct = default);
        Task<MpPreapproval> AlterarValorAsync(CredencialRecebedora zippy, string id, decimal valor, CancellationToken ct = default);
        Task<MpAuthorizedPayment> ObterCobrancaAsync(CredencialRecebedora zippy, long authorizedPaymentId, CancellationToken ct = default);
        Task<IReadOnlyList<MpAuthorizedPayment>> ListarCobrancasAsync(CredencialRecebedora zippy, string preapprovalId, CancellationToken ct = default);
    }

    public sealed class MercadoPagoPreapprovalClient : IMercadoPagoPreapprovalClient
    {
        private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
        private readonly IHttpClientFactory _factory;
        private readonly ILogger<MercadoPagoPreapprovalClient> _logger;

        public MercadoPagoPreapprovalClient(IHttpClientFactory factory, ILogger<MercadoPagoPreapprovalClient> logger)
        {
            _factory = factory;
            _logger = logger;
        }

        public Task<MpPreapproval> CriarAsync(CredencialRecebedora z, MpCriarPreapproval corpo, string chave, CancellationToken ct = default) =>
            EnviarAsync<MpPreapproval>(Zippy(z), HttpMethod.Post, "preapproval", corpo, chave, "criar_preapproval", ct);

        public Task<MpPreapproval> ObterAsync(CredencialRecebedora z, string id, CancellationToken ct = default) =>
            EnviarAsync<MpPreapproval>(Zippy(z), HttpMethod.Get, $"preapproval/{Uri.EscapeDataString(id)}", null, null, "obter_preapproval", ct);

        public Task<MpPreapproval> CancelarAsync(CredencialRecebedora z, string id, CancellationToken ct = default) =>
            EnviarAsync<MpPreapproval>(Zippy(z), HttpMethod.Put, $"preapproval/{Uri.EscapeDataString(id)}", new { status = "cancelled" }, null, "cancelar_preapproval", ct);

        public Task<MpPreapproval> AlterarValorAsync(CredencialRecebedora z, string id, decimal valor, CancellationToken ct = default) =>
            EnviarAsync<MpPreapproval>(Zippy(z), HttpMethod.Put, $"preapproval/{Uri.EscapeDataString(id)}",
                new { auto_recurring = new { transaction_amount = valor, currency_id = "BRL" } }, null, "valor_preapproval", ct);

        public Task<MpAuthorizedPayment> ObterCobrancaAsync(CredencialRecebedora z, long id, CancellationToken ct = default) =>
            EnviarAsync<MpAuthorizedPayment>(Zippy(z), HttpMethod.Get, $"authorized_payments/{id}", null, null, "obter_authorized_payment", ct);

        public async Task<IReadOnlyList<MpAuthorizedPayment>> ListarCobrancasAsync(CredencialRecebedora z, string preapprovalId, CancellationToken ct = default) =>
            (await EnviarAsync<MpBuscaAuthorized>(Zippy(z), HttpMethod.Get,
                $"authorized_payments/search?preapproval_id={Uri.EscapeDataString(preapprovalId)}&limit=20", null, null, "buscar_authorized_payments", ct)).Results;

        private static string Zippy(CredencialRecebedora c)
        {
            // Mensalidade so cai na conta da Zippy, nunca na da loja.
            if (c.Contexto != ContextoCredencial.RecebedorZippy)
                throw new PagamentoException(PagamentoCodigos.CredencialIncompativel, 500, "Assinatura so usa a conta recebedora da Zippy.");
            return c.AccessToken;
        }

        private async Task<T> EnviarAsync<T>(string token, HttpMethod metodo, string caminho, object? corpo, string? chave, string op, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(token)) throw PagamentoException.NaoConfigurado("Conta recebedora da Zippy sem token.");
            using var req = new HttpRequestMessage(metodo, caminho);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (chave != null) req.Headers.TryAddWithoutValidation("X-Idempotency-Key", chave);
            if (corpo != null) req.Content = JsonContent.Create(corpo, corpo.GetType());
            HttpResponseMessage resp;
            try
            {
                resp = await _factory.CreateClient(MercadoPagoClient.NomeHttpClient).SendAsync(req, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _logger.LogWarning("[mp.assinatura] ev=sem_resposta op={Op}", op);
                throw new MercadoPagoApiException(0, null, $"Sem resposta do Mercado Pago ({op}).", ex);
            }
            using (resp)
            {
                var texto = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    string? erro = null;
                    try { using var d = JsonDocument.Parse(texto); if (d.RootElement.TryGetProperty("error", out var e)) erro = e.ToString(); }
                    catch (JsonException) { }
                    _logger.LogWarning("[mp.assinatura] ev=erro op={Op} http={Http} mp_erro={Erro}", op, (int)resp.StatusCode, erro);
                    throw new MercadoPagoApiException((int)resp.StatusCode, erro, $"Mercado Pago respondeu {(int)resp.StatusCode} em {op}.");
                }
                try
                {
                    return JsonSerializer.Deserialize<T>(texto, Json) ?? throw new MercadoPagoApiException(502, "corpo_vazio", $"Resposta vazia em {op}.");
                }
                catch (JsonException ex)
                {
                    throw new MercadoPagoApiException(502, "json_invalido", $"Resposta invalida em {op}.", ex);
                }
            }
        }
    }
}
