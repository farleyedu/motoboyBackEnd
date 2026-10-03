using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class LocalizacaoServiceTests
    {
        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
            public List<string> Urls { get; } = new();

            public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Urls.Add(request.RequestUri!.ToString());
                return Task.FromResult(_responder(request));
            }
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body) };

        private static string Google(string locationType, bool numero = true, bool parcial = false) => $@"{{
  ""status"": ""OK"",
  ""results"": [{{
    ""partial_match"": {(parcial ? "true" : "false")},
    ""address_components"": [{{ ""types"": [{(numero ? "\"street_number\"" : "\"route\"")}] }}],
    ""geometry"": {{ ""location"": {{ ""lat"": -18.9186, ""lng"": -48.2772 }}, ""location_type"": ""{locationType}"" }}
  }}]
}}";

        private static LocalizacaoService Build(FakeHandler handler, string? key = "chave-teste") =>
            new(new HttpClient(handler),
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Google:MapsApiKey"] = key }).Build(),
                NullLogger<LocalizacaoService>.Instance);

        [Theory]
        [InlineData("ROOFTOP", true, false, true)]
        [InlineData("RANGE_INTERPOLATED", true, false, true)]
        [InlineData("GEOMETRIC_CENTER", true, false, false)]
        [InlineData("APPROXIMATE", true, false, false)]
        [InlineData("ROOFTOP", false, false, false)]
        [InlineData("ROOFTOP", true, true, false)]
        public async Task Only_a_found_street_number_without_partial_match_is_exact(string tipo, bool numero, bool parcial, bool esperado)
        {
            var service = Build(new FakeHandler(_ => Json(Google(tipo, numero, parcial))));

            var resultado = await service.GeocodificarAsync("Rua A, 10, Uberlandia");

            Assert.NotNull(resultado);
            Assert.Equal(esperado, resultado!.Exata);
            Assert.Equal("-18.918600", resultado.Latitude);
        }

        [Fact]
        public async Task The_cep_restricts_the_search_and_the_key_is_sent()
        {
            var handler = new FakeHandler(_ => Json(Google("ROOFTOP")));

            await Build(handler).GeocodificarAsync("Rua A, 10", "38407661");

            Assert.Contains("postal_code%3A38407-661", handler.Urls[0]);
            Assert.Contains("key=chave-teste", handler.Urls[0]);
        }

        [Theory]
        [InlineData("{\"status\":\"ZERO_RESULTS\",\"results\":[]}")]
        [InlineData("{\"status\":\"REQUEST_DENIED\",\"error_message\":\"chave invalida\",\"results\":[]}")]
        [InlineData("{\"status\":\"OVER_QUERY_LIMIT\",\"results\":[]}")]
        public async Task Anything_but_ok_is_no_result(string corpo)
        {
            var resultado = await Build(new FakeHandler(_ => Json(corpo))).GeocodificarAsync("Rua A, 10");

            Assert.Null(resultado);
        }

        [Fact]
        public async Task Reverse_geocoding_reads_street_number_neighborhood_city_state_and_cep()
        {
            const string corpo = @"{ ""status"": ""OK"", ""results"": [{ ""address_components"": [
                { ""long_name"": ""120"", ""short_name"": ""120"", ""types"": [""street_number""] },
                { ""long_name"": ""Rua das Flores"", ""short_name"": ""R. das Flores"", ""types"": [""route""] },
                { ""long_name"": ""Centro"", ""short_name"": ""Centro"", ""types"": [""sublocality_level_1"", ""sublocality""] },
                { ""long_name"": ""Uberlandia"", ""short_name"": ""Uberlandia"", ""types"": [""administrative_area_level_2""] },
                { ""long_name"": ""Minas Gerais"", ""short_name"": ""MG"", ""types"": [""administrative_area_level_1""] },
                { ""long_name"": ""38400-000"", ""short_name"": ""38400-000"", ""types"": [""postal_code""] }] }] }";
            var handler = new FakeHandler(_ => Json(corpo));

            var endereco = await Build(handler).GeocodificarReversoAsync(-18.9186, -48.2772);

            Assert.Equal(new EnderecoReverso("Rua das Flores", "120", "Centro", "Uberlandia", "MG", "38400-000"), endereco);
            Assert.Contains("latlng=-18.918600,-48.277200", handler.Urls[0]);
        }

        [Theory]
        [InlineData("{\"status\":\"ZERO_RESULTS\",\"results\":[]}")]
        [InlineData("{\"status\":\"REQUEST_DENIED\",\"results\":[]}")]
        public async Task Reverse_geocoding_without_an_answer_is_null(string corpo)
        {
            Assert.Null(await Build(new FakeHandler(_ => Json(corpo))).GeocodificarReversoAsync(-18.9, -48.2));
            Assert.Null(await Build(new FakeHandler(_ => Json(corpo)), key: "").GeocodificarReversoAsync(-18.9, -48.2));
        }

        [Fact]
        public async Task Without_a_key_nothing_is_called_for_the_exact_search()
        {
            var handler = new FakeHandler(_ => Json(Google("ROOFTOP")));

            var resultado = await Build(handler, key: "").GeocodificarAsync("Rua A, 10");

            Assert.Null(resultado);
            Assert.Empty(handler.Urls);
        }

        [Fact]
        public async Task Internal_screens_still_get_a_point_from_google_even_when_it_is_not_exact()
        {
            var service = Build(new FakeHandler(_ => Json(Google("GEOMETRIC_CENTER"))));

            var coordenadas = await service.ObterCoordenadasAsync("Rua A, Uberlandia");

            Assert.Equal(("-18.918600", "-48.277200"), coordenadas);
        }
    }
}
