
using APIBack.Service.Interface;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace APIBack.Service
{
    /// <summary>
    /// Endereco -> coordenada. Tenta o OpenCage (quando ha chave) e, se ele nao responder com uma posicao, cai no
    /// Nominatim (OpenStreetMap), que nao exige chave. Cada falha vai para o log com o motivo.
    /// </summary>
    public class LocalizacaoService : ILocalizacaoService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<LocalizacaoService> _logger;
        private readonly string _apiKey;
        private const string OpenCageUrl = "https://api.opencagedata.com/geocode/v1/json";
        private const string NominatimUrl = "https://nominatim.openstreetmap.org/search";
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan OpenCageCooldown = TimeSpan.FromMinutes(10);

        // Chave recusada (401/402/403/429): nao insiste a cada pedido, usa so o Nominatim por um tempo.
        private static DateTime _openCageBlockedUntil = DateTime.MinValue;

        public LocalizacaoService(HttpClient httpClient, IConfiguration configuration, ILogger<LocalizacaoService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _apiKey = configuration["OpenCage:ApiKey"] ?? string.Empty;
        }

        public async Task<(string Latitude, string Longitude)?> ObterCoordenadasAsync(string endereco)
        {
            if (string.IsNullOrWhiteSpace(endereco)) return null;

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                _logger.LogWarning("Geocodificacao: OpenCage:ApiKey vazia; usando so o Nominatim.");
            }
            else if (DateTime.UtcNow >= _openCageBlockedUntil)
            {
                var viaOpenCage = await TentarAsync("OpenCage", () => BuscarOpenCageAsync(endereco));
                if (viaOpenCage != null) return viaOpenCage;
            }

            return await TentarAsync("Nominatim", () => BuscarNominatimAsync(endereco));
        }

        private async Task<(string Latitude, string Longitude)?> TentarAsync(
            string provedor,
            Func<Task<(string Latitude, string Longitude)?>> busca)
        {
            try
            {
                return await busca();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Geocodificacao: {Provedor} falhou.", provedor);
                return null;
            }
        }

        private async Task<(string Latitude, string Longitude)?> BuscarOpenCageAsync(string endereco)
        {
            var url = $"{OpenCageUrl}?q={Uri.EscapeDataString(endereco)}&key={_apiKey}&language=pt&countrycode=br&limit=1";

            using var cts = new CancellationTokenSource(RequestTimeout);
            using var response = await _httpClient.GetAsync(url, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired
                    or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    _openCageBlockedUntil = DateTime.UtcNow + OpenCageCooldown;
                }
                _logger.LogWarning("Geocodificacao: OpenCage respondeu {Status}.", (int)response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cts.Token);
            using var json = JsonDocument.Parse(content);
            var results = json.RootElement.GetProperty("results");
            if (results.GetArrayLength() == 0)
            {
                _logger.LogInformation("Geocodificacao: OpenCage nao achou o endereco.");
                return null;
            }

            var geometry = results[0].GetProperty("geometry");
            return Formatar(geometry.GetProperty("lat").GetDouble(), geometry.GetProperty("lng").GetDouble());
        }

        private async Task<(string Latitude, string Longitude)?> BuscarNominatimAsync(string endereco)
        {
            var url = $"{NominatimUrl}?q={Uri.EscapeDataString(endereco)}&format=jsonv2&limit=1&countrycodes=br&accept-language=pt-BR";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // A politica de uso do Nominatim exige um identificador do aplicativo.
            request.Headers.UserAgent.ParseAdd("ZippyGo/1.0 (cardapio-web; contato@zippygo.com.br)");

            using var cts = new CancellationTokenSource(RequestTimeout);
            using var response = await _httpClient.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Geocodificacao: Nominatim respondeu {Status}.", (int)response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cts.Token);
            using var json = JsonDocument.Parse(content);
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() == 0)
            {
                _logger.LogInformation("Geocodificacao: Nominatim nao achou o endereco.");
                return null;
            }

            var first = json.RootElement[0];
            if (!double.TryParse(first.GetProperty("lat").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                || !double.TryParse(first.GetProperty("lon").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lng))
            {
                return null;
            }

            return Formatar(lat, lng);
        }

        private static (string Latitude, string Longitude) Formatar(double lat, double lng) =>
            (lat.ToString("F6", CultureInfo.InvariantCulture), lng.ToString("F6", CultureInfo.InvariantCulture));
    }
}
