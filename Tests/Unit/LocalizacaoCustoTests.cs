using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace APIBack.Tests.Unit
{
    /// <summary>Cache, limite mensal e falha controlada da geocodificacao: o que impede cobranca do Google.</summary>
    public class LocalizacaoCustoTests
    {
        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
            public int Chamadas { get; private set; }

            public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Chamadas++;
                return Task.FromResult(_responder(request));
            }
        }

        private sealed class ContadorFalso : IGoogleUsoContador
        {
            public int Restantes { get; set; } = int.MaxValue;
            public bool Falha { get; set; }
            public int Pedidos { get; private set; }

            public Task<bool> TentarReservarAsync(string servico, int limiteMensal, CancellationToken cancellationToken = default)
            {
                Pedidos++;
                if (Falha) throw new InvalidOperationException("contador com defeito");
                if (Restantes <= 0) return Task.FromResult(false);
                Restantes--;
                return Task.FromResult(true);
            }

            public Task<GoogleUsoResumo> ObterAsync(string servico, int limiteMensal, CancellationToken cancellationToken = default) =>
                Task.FromResult(new GoogleUsoResumo(servico, "2026-10", Pedidos, limiteMensal));
        }

        private const string Achou = @"{ ""status"": ""OK"", ""results"": [{ ""partial_match"": false,
            ""address_components"": [{ ""types"": [""street_number""] }],
            ""geometry"": { ""location"": { ""lat"": -18.9186, ""lng"": -48.2772 }, ""location_type"": ""ROOFTOP"" } }] }";

        private const string Reverso = @"{ ""status"": ""OK"", ""results"": [{ ""address_components"": [
            { ""long_name"": ""Rua das Flores"", ""short_name"": ""R. das Flores"", ""types"": [""route""] }] }] }";

        private static HttpResponseMessage Json(string corpo) => new(HttpStatusCode.OK) { Content = new StringContent(corpo) };

        private static (LocalizacaoService Servico, MemoryCache Cache) Montar(FakeHandler handler, IGoogleUsoContador? contador, Dictionary<string, string?>? extra = null)
        {
            var configuracao = new Dictionary<string, string?> { ["Google:MapsApiKey"] = "chave-teste" };
            if (extra != null) foreach (var par in extra) configuracao[par.Key] = par.Value;
            var cache = new MemoryCache(new MemoryCacheOptions());
            var servico = new LocalizacaoService(new HttpClient(handler),
                new ConfigurationBuilder().AddInMemoryCollection(configuracao).Build(),
                NullLogger<LocalizacaoService>.Instance, cache, contador);
            return (servico, cache);
        }

        [Fact]
        public async Task The_same_address_with_other_spacing_or_case_costs_one_google_call()
        {
            var handler = new FakeHandler(_ => Json(Achou));
            var contador = new ContadorFalso();
            var (servico, _) = Montar(handler, contador);

            await servico.ConsultarEnderecoAsync("Rua A, 10, Uberlandia", "38407661");
            var segunda = await servico.ConsultarEnderecoAsync("  rua a,   10,  UBERLANDIA ", "38407-661");

            Assert.Equal("found", segunda.Status);
            Assert.Equal(1, handler.Chamadas);
            Assert.Equal(1, contador.Pedidos);
        }

        [Fact]
        public async Task Different_addresses_are_not_mixed_up()
        {
            var handler = new FakeHandler(_ => Json(Achou));
            var (servico, _) = Montar(handler, new ContadorFalso());

            await servico.ConsultarEnderecoAsync("Rua A, 10");
            await servico.ConsultarEnderecoAsync("Rua A, 11");

            Assert.Equal(2, handler.Chamadas);
        }

        [Fact]
        public async Task An_unknown_address_is_remembered_but_a_provider_failure_is_not()
        {
            var resposta = "{\"status\":\"ZERO_RESULTS\",\"results\":[]}";
            var handler = new FakeHandler(_ => Json(resposta));
            var (servico, _) = Montar(handler, new ContadorFalso());

            await servico.ConsultarEnderecoAsync("Rua Inexistente, 1");
            var repetida = await servico.ConsultarEnderecoAsync("Rua Inexistente, 1");
            Assert.Equal("not_found", repetida.Status);
            Assert.Equal(1, handler.Chamadas);

            resposta = "{\"status\":\"REQUEST_DENIED\",\"results\":[]}";
            await servico.ConsultarEnderecoAsync("Rua B, 2");
            var denovo = await servico.ConsultarEnderecoAsync("Rua B, 2");
            Assert.Equal("unavailable", denovo.Status);
            Assert.Equal(3, handler.Chamadas);
        }

        [Fact]
        public async Task At_the_monthly_limit_google_is_not_called_and_the_answer_is_unavailable()
        {
            var handler = new FakeHandler(_ => Json(Achou));
            var contador = new ContadorFalso { Restantes = 1 };
            var (servico, _) = Montar(handler, contador);

            var primeira = await servico.ConsultarEnderecoAsync("Rua A, 10");
            var segunda = await servico.ConsultarEnderecoAsync("Rua C, 30");

            Assert.Equal("found", primeira.Status);
            Assert.Equal("unavailable", segunda.Status);
            Assert.Null(segunda.Resultado);
            Assert.Equal(1, handler.Chamadas);
        }

        [Fact]
        public async Task A_cached_address_still_works_after_the_limit_is_reached()
        {
            var handler = new FakeHandler(_ => Json(Achou));
            var contador = new ContadorFalso { Restantes = 1 };
            var (servico, _) = Montar(handler, contador);
            await servico.ConsultarEnderecoAsync("Rua A, 10");

            var repetida = await servico.ConsultarEnderecoAsync("Rua A, 10");

            Assert.Equal("found", repetida.Status);
            Assert.Equal(1, handler.Chamadas);
        }

        [Fact]
        public async Task A_broken_counter_blocks_google_instead_of_spending()
        {
            var handler = new FakeHandler(_ => Json(Achou));
            var (servico, _) = Montar(handler, new ContadorFalso { Falha = true });

            var resultado = await servico.ConsultarEnderecoAsync("Rua A, 10");

            Assert.Equal("unavailable", resultado.Status);
            Assert.Equal(0, handler.Chamadas);
        }

        [Fact]
        public async Task Reverse_geocoding_uses_the_cache_and_the_same_counter()
        {
            var handler = new FakeHandler(_ => Json(Reverso));
            var contador = new ContadorFalso();
            var (servico, _) = Montar(handler, contador);

            var um = await servico.GeocodificarReversoAsync(-18.918600, -48.277200);
            var dois = await servico.GeocodificarReversoAsync(-18.9186001, -48.2772002);

            Assert.Equal("Rua das Flores", um!.Logradouro);
            Assert.Equal("Rua das Flores", dois!.Logradouro);
            Assert.Equal(1, handler.Chamadas);
            Assert.Equal(1, contador.Pedidos);
        }

        [Fact]
        public async Task Reverse_geocoding_stops_at_the_monthly_limit()
        {
            var handler = new FakeHandler(_ => Json(Reverso));
            var (servico, _) = Montar(handler, new ContadorFalso { Restantes = 0 });

            Assert.Null(await servico.GeocodificarReversoAsync(-18.9, -48.2));
            Assert.Equal(0, handler.Chamadas);
        }

        [Fact]
        public async Task Without_a_key_nothing_is_counted()
        {
            var handler = new FakeHandler(_ => Json(Achou));
            var contador = new ContadorFalso();
            var (servico, _) = Montar(handler, contador, new Dictionary<string, string?> { ["Google:MapsApiKey"] = "" });

            var resultado = await servico.ConsultarEnderecoAsync("Rua A, 10");

            Assert.Equal("unavailable", resultado.Status);
            Assert.Equal(0, contador.Pedidos);
            Assert.Equal(0, handler.Chamadas);
        }
    }
}
