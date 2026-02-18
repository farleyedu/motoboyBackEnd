
using APIBack.Service.Interface;
using Microsoft.Extensions.Configuration;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace APIBack.Service
{
    public class LocalizacaoService : ILocalizacaoService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private const string BaseUrl = "https://api.opencagedata.com/geocode/v1/json";

        public LocalizacaoService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _apiKey = configuration["OpenCage:ApiKey"] ?? string.Empty;
        }

        public async Task<(string Latitude, string Longitude)?> ObterCoordenadasAsync(string endereco)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                return null;
            }

            var url = $"{BaseUrl}?q={Uri.EscapeDataString(endereco)}&key={_apiKey}&language=pt&limit=1";

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(content);
            var results = json.RootElement.GetProperty("results");

            if (results.GetArrayLength() == 0) return null;

            var geometry = results[0].GetProperty("geometry");
            var lat = geometry.GetProperty("lat").GetDouble().ToString("F6", CultureInfo.InvariantCulture);
            var lng = geometry.GetProperty("lng").GetDouble().ToString("F6", CultureInfo.InvariantCulture);

            return (lat, lng);
        }

    }
}
