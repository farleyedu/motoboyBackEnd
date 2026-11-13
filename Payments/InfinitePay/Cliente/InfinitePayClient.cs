using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace APIBack.Payments.InfinitePay.Cliente
{
    /// <summary>
    /// Cliente da InfinitePay. POST /links recebe o corpo JA serializado para que uma repeticao envie exatamente os mesmos bytes (a
    /// InfinitePay pode devolver o mesmo link; nao e promessa). A API Key vai so no header X-Api-Key desta requisicao e nunca e logada.
    /// </summary>
    public interface IInfinitePayClient
    {
        Task<InfinitePayLinkResposta> CriarLinkAsync(string corpoJson, string? apiKey, CancellationToken ct = default);
        /// <summary>POST /payment_check: unica prova de pagamento aceita. O handle vem da conta gravada no servidor, nunca do aviso.</summary>
        Task<InfinitePayPaymentCheck> VerificarPagamentoAsync(string handle, string orderNsu, string transactionNsu, string slug, string? apiKey, CancellationToken ct = default);
    }

    public sealed class InfinitePayClient : IInfinitePayClient
    {
        public const string NomeHttpClient = "infinitepay";
        private static readonly string[] CamposUrl = { "url", "link", "checkout_url", "checkout_link", "payment_url", "url_checkout" };
        private static readonly Regex CodigoErro = new("^[a-z0-9_]{1,80}$", RegexOptions.Compiled);

        private readonly IHttpClientFactory _factory;
        private readonly InfinitePayOptions _options;
        private readonly ILogger<InfinitePayClient> _logger;

        public InfinitePayClient(IHttpClientFactory factory, IOptions<InfinitePayOptions> options, ILogger<InfinitePayClient> logger)
        {
            _factory = factory;
            _options = options.Value;
            _logger = logger;
        }

        public static string Serializar(InfinitePayCriarLinkRequest request) => JsonSerializer.Serialize(request);

        public Task<InfinitePayLinkResposta> CriarLinkAsync(string corpoJson, string? apiKey, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(corpoJson)) throw new ArgumentException("Corpo obrigatorio.", nameof(corpoJson));
            return ComRetryAsync(async () =>
            {
                var (status, corpo) = await EnviarAsync("links", corpoJson, apiKey, ct);
                if (status is < 200 or >= 300)
                    throw new InfinitePayApiException(status, LerCodigoErro(corpo), $"InfinitePay respondeu {status} em /links.");
                var url = LerUrl(corpo);
                if (url == null)
                    throw new InfinitePayApiException(status, "resposta_sem_url", "Resposta da InfinitePay sem URL de checkout valida.");
                return new InfinitePayLinkResposta
                {
                    Url = url,
                    RespostaHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(corpo))).ToLowerInvariant()
                };
            }, "links", ct);
        }

        public Task<InfinitePayPaymentCheck> VerificarPagamentoAsync(string handle, string orderNsu, string transactionNsu, string slug, string? apiKey, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(handle) || string.IsNullOrWhiteSpace(orderNsu) || string.IsNullOrWhiteSpace(transactionNsu) || string.IsNullOrWhiteSpace(slug))
                throw new ArgumentException("handle, order_nsu, transaction_nsu e slug sao obrigatorios.");
            var corpoJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["handle"] = handle, ["order_nsu"] = orderNsu, ["transaction_nsu"] = transactionNsu, ["slug"] = slug
            });
            // Consulta de leitura: repetir e seguro.
            return ComRetryAsync(async () =>
            {
                var (status, corpo) = await EnviarAsync("payment_check", corpoJson, apiKey, ct);
                if (status is < 200 or >= 300)
                    throw new InfinitePayApiException(status, LerCodigoErro(corpo), $"InfinitePay respondeu {status} em /payment_check.");
                return InfinitePayPaymentCheck.Ler(corpo)
                       ?? throw new InfinitePayApiException(status, "resposta_invalida", "Resposta de /payment_check invalida.");
            }, "payment_check", ct);
        }

        private async Task<T> ComRetryAsync<T>(Func<Task<T>> acao, string rota, CancellationToken ct)
        {
            var tentativas = Math.Clamp(_options.MaxTentativas, 1, 3);
            InfinitePayApiException? ultima = null;
            for (var n = 1; n <= tentativas; n++)
            {
                try
                {
                    return await acao();
                }
                catch (InfinitePayApiException ex) when (ex.Indisponivel && n < tentativas)
                {
                    ultima = ex;
                    _logger.LogWarning("InfinitePay /{Rota} sem sucesso (http {Status}), tentativa {N}/{Max}.", rota, ex.StatusHttp, n, tentativas);
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * n), ct);
                }
            }
            throw ultima!;
        }

        private async Task<(int Status, string Corpo)> EnviarAsync(string rota, string corpoJson, string? apiKey, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, rota) { Content = new StringContent(corpoJson, Encoding.UTF8, "application/json") };
            if (!string.IsNullOrWhiteSpace(apiKey)) req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);

            try
            {
                using var http = _factory.CreateClient(NomeHttpClient);
                using var resp = await http.SendAsync(req, ct);
                return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                throw new InfinitePayApiException(0, null, "Sem resposta da InfinitePay (rede ou tempo esgotado).", ex);
            }
        }

        /// <summary>O nome do campo da URL nao esta documentado: aceita os nomes provaveis (raiz ou dentro de "data") e exige HTTPS.</summary>
        public static string? LerUrl(string corpo)
        {
            try
            {
                using var doc = JsonDocument.Parse(corpo);
                var raiz = doc.RootElement;
                if (raiz.ValueKind == JsonValueKind.String) return UrlHttps(raiz.GetString());
                if (raiz.ValueKind != JsonValueKind.Object) return null;
                var achada = Procurar(raiz);
                if (achada != null) return achada;
                if (raiz.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object) return Procurar(data);
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? Procurar(JsonElement obj)
        {
            foreach (var nome in CamposUrl)
                if (obj.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String && UrlHttps(v.GetString()) is { } url)
                    return url;
            return null;
        }

        private static string? UrlHttps(string? valor) =>
            Uri.TryCreate(valor, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) ? uri.AbsoluteUri : null;

        private static string? LerCodigoErro(string corpo)
        {
            try
            {
                using var doc = JsonDocument.Parse(corpo);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                foreach (var nome in new[] { "error", "code" })
                    if (doc.RootElement.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s && CodigoErro.IsMatch(s))
                        return s;
            }
            catch (JsonException)
            {
            }
            return null;
        }
    }
}
