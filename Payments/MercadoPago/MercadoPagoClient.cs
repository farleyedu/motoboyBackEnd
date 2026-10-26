using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace APIBack.Payments.MercadoPago
{
    /// <summary>Resposta de erro do MP. A mensagem e curta e nunca contem o token.</summary>
    public sealed class MercadoPagoApiException : Exception
    {
        public MercadoPagoApiException(int statusHttp, string? mpErro, string mensagem, Exception? inner = null) : base(mensagem, inner)
        {
            StatusHttp = statusHttp;
            MpErro = mpErro;
        }

        /// <summary>0 = sem resposta (rede/timeout): resultado desconhecido, repetir com a MESMA chave de idempotencia.</summary>
        public int StatusHttp { get; }
        public string? MpErro { get; }
        public bool Indisponivel => StatusHttp == 0 || StatusHttp >= 500 || StatusHttp == 429;

        public PagamentoException ParaPagamento() => Indisponivel
            ? new PagamentoException(PagamentoCodigos.MercadoPagoIndisponivel, 503, "Mercado Pago indisponivel no momento. Tente de novo.", inner: this)
            : new PagamentoException(PagamentoCodigos.MercadoPagoErro, 502, "O Mercado Pago recusou a operacao.",
                new Dictionary<string, object?> { ["mpStatus"] = StatusHttp, ["mpErro"] = MpErro }, this);
    }

    /// <summary>
    /// Cliente HTTP do MP. Pagamentos so aceitam <see cref="CredencialRecebedora"/> (Zippy ou loja); OAuth so aceita
    /// <see cref="CredencialAplicacao"/>. O token vai so no header Authorization desta requisicao e nunca e logado.
    /// </summary>
    public interface IMercadoPagoClient
    {
        Task<MpPagamento> CriarPagamentoAsync(CredencialRecebedora credencial, MpCriarPagamentoRequest request, string idempotencyKey, CancellationToken ct = default);
        Task<MpPagamento> ObterPagamentoAsync(CredencialRecebedora credencial, long mpPaymentId, CancellationToken ct = default);
        Task<IReadOnlyList<MpPagamento>> BuscarPorReferenciaAsync(CredencialRecebedora credencial, string externalReference, CancellationToken ct = default);
        Task<MpPagamento> CancelarPagamentoAsync(CredencialRecebedora credencial, long mpPaymentId, CancellationToken ct = default);
        Task<MpRefund> EstornarAsync(CredencialRecebedora credencial, long mpPaymentId, decimal? valor, string idempotencyKey, CancellationToken ct = default);
        Task<MpChargeback> ObterChargebackAsync(CredencialRecebedora credencial, string chargebackId, CancellationToken ct = default);
        Task<MpOAuthToken> TrocarCodigoOAuthAsync(CredencialAplicacao app, string code, string redirectUri, string? codeVerifier, CancellationToken ct = default);
        Task<MpOAuthToken> RenovarTokenAsync(CredencialAplicacao app, string refreshToken, CancellationToken ct = default);
        /// <summary>GET /users/me: dono do token (id, situacao da conta). Base das verificacoes da Gestao e da loja.</summary>
        Task<MpUsuario> ObterUsuarioAsync(CredencialRecebedora credencial, CancellationToken ct = default);
        /// <summary>GET /v1/payment_methods: meios liberados na conta (sem chave Pix cadastrada o Pix nao aparece).</summary>
        Task<IReadOnlyList<MpMeioPagamento>> MeiosDePagamentoAsync(CredencialRecebedora credencial, CancellationToken ct = default);
        /// <summary>Confere client_id/secret da aplicacao (grant client_credentials). Devolve o user_id do dono; o token e descartado.</summary>
        Task<long> ValidarAplicacaoAsync(CredencialAplicacao app, CancellationToken ct = default);
    }

    public sealed class MercadoPagoClient : IMercadoPagoClient
    {
        public const string NomeHttpClient = "mercadopago";
        private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
        private readonly IHttpClientFactory _factory;
        private readonly ILogger<MercadoPagoClient> _logger;

        public MercadoPagoClient(IHttpClientFactory factory, ILogger<MercadoPagoClient> logger)
        {
            _factory = factory;
            _logger = logger;
        }

        public Task<MpPagamento> CriarPagamentoAsync(CredencialRecebedora c, MpCriarPagamentoRequest request, string idempotencyKey, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new ArgumentException("Chave de idempotencia obrigatoria.", nameof(idempotencyKey));
            return EnviarAsync<MpPagamento>(c.AccessToken, HttpMethod.Post, "v1/payments", request, idempotencyKey, "criar_pagamento", ct);
        }

        public Task<MpPagamento> ObterPagamentoAsync(CredencialRecebedora c, long id, CancellationToken ct = default) =>
            EnviarAsync<MpPagamento>(c.AccessToken, HttpMethod.Get, $"v1/payments/{id}", null, null, "obter_pagamento", ct);

        public async Task<IReadOnlyList<MpPagamento>> BuscarPorReferenciaAsync(CredencialRecebedora c, string externalReference, CancellationToken ct = default)
        {
            var r = await EnviarAsync<MpBuscaPagamentos>(c.AccessToken, HttpMethod.Get,
                $"v1/payments/search?external_reference={Uri.EscapeDataString(externalReference)}&sort=date_created&criteria=desc&limit=20",
                null, null, "buscar_pagamentos", ct);
            return r.Results;
        }

        public Task<MpPagamento> CancelarPagamentoAsync(CredencialRecebedora c, long id, CancellationToken ct = default) =>
            EnviarAsync<MpPagamento>(c.AccessToken, HttpMethod.Put, $"v1/payments/{id}", new { status = "cancelled" }, null, "cancelar_pagamento", ct);

        public Task<MpRefund> EstornarAsync(CredencialRecebedora c, long id, decimal? valor, string idempotencyKey, CancellationToken ct = default) =>
            EnviarAsync<MpRefund>(c.AccessToken, HttpMethod.Post, $"v1/payments/{id}/refunds",
                valor.HasValue ? new { amount = valor.Value } : new { }, idempotencyKey, "estornar", ct);

        public Task<MpChargeback> ObterChargebackAsync(CredencialRecebedora c, string chargebackId, CancellationToken ct = default) =>
            EnviarAsync<MpChargeback>(c.AccessToken, HttpMethod.Get, $"v1/chargebacks/{Uri.EscapeDataString(chargebackId)}", null, null, "obter_chargeback", ct);

        public Task<MpOAuthToken> TrocarCodigoOAuthAsync(CredencialAplicacao app, string code, string redirectUri, string? codeVerifier, CancellationToken ct = default)
        {
            var corpo = new Dictionary<string, string>
            {
                ["client_id"] = app.ClientId, ["client_secret"] = app.ClientSecret, ["grant_type"] = "authorization_code",
                ["code"] = code, ["redirect_uri"] = redirectUri
            };
            if (!string.IsNullOrWhiteSpace(codeVerifier)) corpo["code_verifier"] = codeVerifier!;
            return EnviarAsync<MpOAuthToken>(null, HttpMethod.Post, "oauth/token", corpo, null, "oauth_codigo", ct);
        }

        public Task<MpOAuthToken> RenovarTokenAsync(CredencialAplicacao app, string refreshToken, CancellationToken ct = default) =>
            EnviarAsync<MpOAuthToken>(null, HttpMethod.Post, "oauth/token", new Dictionary<string, string>
            {
                ["client_id"] = app.ClientId, ["client_secret"] = app.ClientSecret, ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken
            }, null, "oauth_refresh", ct);

        public Task<MpUsuario> ObterUsuarioAsync(CredencialRecebedora c, CancellationToken ct = default) =>
            EnviarAsync<MpUsuario>(c.AccessToken, HttpMethod.Get, "users/me", null, null, "usuario", ct);

        public async Task<IReadOnlyList<MpMeioPagamento>> MeiosDePagamentoAsync(CredencialRecebedora c, CancellationToken ct = default) =>
            await EnviarAsync<List<MpMeioPagamento>>(c.AccessToken, HttpMethod.Get, "v1/payment_methods", null, null, "meios_pagamento", ct);

        public async Task<long> ValidarAplicacaoAsync(CredencialAplicacao app, CancellationToken ct = default)
        {
            var token = await EnviarAsync<MpOAuthToken>(null, HttpMethod.Post, "oauth/token", new Dictionary<string, string>
            {
                ["client_id"] = app.ClientId, ["client_secret"] = app.ClientSecret, ["grant_type"] = "client_credentials"
            }, null, "oauth_aplicacao", ct);
            return token.UserId;
        }

        private async Task<T> EnviarAsync<T>(string? token, HttpMethod metodo, string caminho, object? corpo, string? idempotencyKey, string operacao, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(metodo, caminho);
            if (token != null)
            {
                if (string.IsNullOrWhiteSpace(token)) throw new PagamentoException(PagamentoCodigos.NaoConfigurado, 503, "Credencial sem token.");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            if (idempotencyKey != null) req.Headers.TryAddWithoutValidation("X-Idempotency-Key", idempotencyKey);
            if (corpo != null) req.Content = JsonContent.Create(corpo, corpo.GetType());

            HttpResponseMessage resp;
            try
            {
                resp = await _factory.CreateClient(NomeHttpClient).SendAsync(req, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _logger.LogWarning("[mp.http] ev=sem_resposta op={Op} erro={Erro}", operacao, ex.GetType().Name);
                throw new MercadoPagoApiException(0, null, $"Sem resposta do Mercado Pago ({operacao}).", ex);
            }

            using (resp)
            {
                var requestId = resp.Headers.TryGetValues("x-request-id", out var v) ? string.Join(",", v) : null;
                var texto = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var erro = ExtrairErro(texto);
                    _logger.LogWarning("[mp.http] ev=erro op={Op} http={Http} mp_erro={Erro} req={Req}", operacao, (int)resp.StatusCode, erro, requestId);
                    throw new MercadoPagoApiException((int)resp.StatusCode, erro, $"Mercado Pago respondeu {(int)resp.StatusCode} em {operacao}.");
                }

                _logger.LogDebug("[mp.http] ev=ok op={Op} http={Http} req={Req}", operacao, (int)resp.StatusCode, requestId);
                try
                {
                    return JsonSerializer.Deserialize<T>(texto, Json)
                        ?? throw new MercadoPagoApiException((int)resp.StatusCode, "corpo_vazio", $"Resposta vazia em {operacao}.");
                }
                catch (JsonException ex)
                {
                    throw new MercadoPagoApiException((int)HttpStatusCode.BadGateway, "json_invalido", $"Resposta invalida em {operacao}.", ex);
                }
            }
        }

        /// <summary>So o codigo do erro (ex.: bad_request, invalid_grant): a mensagem do MP pode ecoar dados do pedido.</summary>
        private static string? ExtrairErro(string? texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return null;
            try
            {
                using var doc = JsonDocument.Parse(texto);
                var raiz = doc.RootElement;
                if (raiz.ValueKind != JsonValueKind.Object) return null;
                if (raiz.TryGetProperty("cause", out var causa) && causa.ValueKind == JsonValueKind.Array && causa.GetArrayLength() > 0 &&
                    causa[0].TryGetProperty("code", out var cod))
                    return $"{(raiz.TryGetProperty("error", out var e1) ? e1.ToString() : "erro")}:{cod}";
                return raiz.TryGetProperty("error", out var e) ? e.ToString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
