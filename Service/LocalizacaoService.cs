
using APIBack.Service.Interface;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace APIBack.Service
{
    /// <summary>
    /// Endereco -> coordenada. O Google Geocoding e a fonte principal e diz se o ponto e exato; sem chave configurada
    /// (ou se ele falhar) as telas internas caem no Nominatim (OpenStreetMap), que nao exige chave mas so acha a rua.
    /// O pedido do cardapio web nunca usa o Nominatim: ele so aceita ponto exato.
    /// </summary>
    public class LocalizacaoService : ILocalizacaoService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<LocalizacaoService> _logger;
        private readonly string _googleKey;
        private const string GoogleUrl = "https://maps.googleapis.com/maps/api/geocode/json";
        private const string NominatimUrl = "https://nominatim.openstreetmap.org/search";
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

        public LocalizacaoService(HttpClient httpClient, IConfiguration configuration, ILogger<LocalizacaoService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _googleKey = configuration["Google:MapsApiKey"] ?? string.Empty;
        }

        public async Task<(string Latitude, string Longitude)?> ObterCoordenadasAsync(string endereco)
        {
            if (string.IsNullOrWhiteSpace(endereco)) return null;

            var google = await GeocodificarAsync(endereco);
            if (google != null) return (google.Latitude, google.Longitude);

            return await TentarAsync("Nominatim", () => BuscarNominatimAsync(endereco));
        }

        public async Task<GeocodeResultado?> GeocodificarAsync(string endereco, string? cep = null)
        {
            if (string.IsNullOrWhiteSpace(endereco)) return null;

            if (string.IsNullOrWhiteSpace(_googleKey))
            {
                _logger.LogWarning("Geocodificacao: Google:MapsApiKey nao configurada.");
                return null;
            }

            try
            {
                return await BuscarGoogleAsync(endereco, cep);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Geocodificacao: Google falhou.");
                return null;
            }
        }

        /// <summary>
        /// Exato = o Google chegou ao numero pedido: ponto na porta (ROOFTOP) ou interpolado entre dois pontos
        /// conhecidos da mesma quadra (RANGE_INTERPOLATED), sem correspondencia parcial. GEOMETRIC_CENTER e
        /// APPROXIMATE (centro da rua, do bairro ou do CEP) nunca sao exatos.
        /// </summary>
        internal static bool EhExato(string? locationType, bool correspondenciaParcial, bool achouNumero) =>
            achouNumero
            && !correspondenciaParcial
            && locationType is "ROOFTOP" or "RANGE_INTERPOLATED";

        public async Task<EnderecoReverso?> GeocodificarReversoAsync(double latitude, double longitude)
        {
            if (string.IsNullOrWhiteSpace(_googleKey))
            {
                _logger.LogWarning("Geocodificacao: Google:MapsApiKey nao configurada.");
                return null;
            }

            try
            {
                var lat = latitude.ToString("F6", CultureInfo.InvariantCulture);
                var lng = longitude.ToString("F6", CultureInfo.InvariantCulture);
                // result_type restringe ao que importa para entrega: endereco da rua/numero, nao regioes grandes.
                var url = $"{GoogleUrl}?latlng={lat},{lng}&language=pt-BR&result_type=street_address|route|premise&key={_googleKey}";

                using var cts = new CancellationTokenSource(RequestTimeout);
                using var response = await _httpClient.GetAsync(url, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Geocodificacao: Google (reverso) respondeu HTTP {Status}.", (int)response.StatusCode);
                    return null;
                }

                return InterpretarReverso(await response.Content.ReadAsStringAsync(cts.Token));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Geocodificacao: Google (reverso) falhou.");
                return null;
            }
        }

        /// <summary>Le a resposta do Google (geocode reverso) e devolve rua, numero, bairro, cidade, UF e CEP quando existirem.</summary>
        internal static EnderecoReverso? InterpretarReverso(string corpo)
        {
            using var json = JsonDocument.Parse(corpo);
            var root = json.RootElement;
            if (!root.TryGetProperty("status", out var status) || status.GetString() != "OK") return null;

            var primeiro = root.GetProperty("results")[0];
            if (!primeiro.TryGetProperty("address_components", out var componentes)) return null;

            string? Tipo(string tipo, bool curto = false)
            {
                foreach (var c in componentes.EnumerateArray())
                {
                    if (c.TryGetProperty("types", out var tipos) && tipos.EnumerateArray().Any(t => t.GetString() == tipo))
                    {
                        return c.GetProperty(curto ? "short_name" : "long_name").GetString();
                    }
                }

                return null;
            }

            return new EnderecoReverso(
                Tipo("route"),
                Tipo("street_number"),
                Tipo("sublocality_level_1") ?? Tipo("sublocality") ?? Tipo("neighborhood"),
                Tipo("administrative_area_level_2") ?? Tipo("locality"),
                Tipo("administrative_area_level_1", curto: true),
                Tipo("postal_code"));
        }

        private async Task<GeocodeResultado?> BuscarGoogleAsync(string endereco, string? cep)
        {
            var components = "country:BR";
            var digitosCep = new string((cep ?? string.Empty).Where(char.IsDigit).ToArray());
            if (digitosCep.Length == 8) components += $"|postal_code:{digitosCep[..5]}-{digitosCep[5..]}";

            var url = $"{GoogleUrl}?address={Uri.EscapeDataString(endereco)}&components={Uri.EscapeDataString(components)}"
                      + $"&language=pt-BR&region=br&key={_googleKey}";

            using var cts = new CancellationTokenSource(RequestTimeout);
            using var response = await _httpClient.GetAsync(url, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Geocodificacao: Google respondeu HTTP {Status}.", (int)response.StatusCode);
                return null;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            var root = json.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;

            if (status != "OK")
            {
                // ZERO_RESULTS e normal (endereco inexistente). Os demais (chave negada, cota, API desligada) sao
                // problema de configuracao e precisam aparecer no log.
                var mensagem = root.TryGetProperty("error_message", out var m) ? m.GetString() : null;
                if (status == "ZERO_RESULTS") _logger.LogInformation("Geocodificacao: Google nao achou o endereco.");
                else _logger.LogError("Geocodificacao: Google respondeu {Status}: {Mensagem}", status, mensagem);
                return null;
            }

            var primeiro = root.GetProperty("results")[0];
            var geometry = primeiro.GetProperty("geometry");
            var location = geometry.GetProperty("location");
            var locationType = geometry.TryGetProperty("location_type", out var lt) ? lt.GetString() : null;
            var parcial = primeiro.TryGetProperty("partial_match", out var pm) && pm.ValueKind == JsonValueKind.True;
            var achouNumero = primeiro.TryGetProperty("address_components", out var comps)
                && comps.EnumerateArray().Any(c => c.TryGetProperty("types", out var tipos)
                    && tipos.EnumerateArray().Any(t => t.GetString() == "street_number"));

            var exata = EhExato(locationType, parcial, achouNumero);
            _logger.LogInformation(
                "Geocodificacao: Google location_type={Tipo} parcial={Parcial} numero={Numero} exata={Exata}",
                locationType, parcial, achouNumero, exata);

            return new GeocodeResultado(
                location.GetProperty("lat").GetDouble().ToString("F6", CultureInfo.InvariantCulture),
                location.GetProperty("lng").GetDouble().ToString("F6", CultureInfo.InvariantCulture),
                exata,
                "google");
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

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
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

            return (lat.ToString("F6", CultureInfo.InvariantCulture), lng.ToString("F6", CultureInfo.InvariantCulture));
        }
    }
}
