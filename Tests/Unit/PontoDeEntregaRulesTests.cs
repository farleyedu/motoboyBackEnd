using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using APIBack.Controllers;
using APIBack.DTOs.Cardapio;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class PontoDeEntregaRulesTests
    {
        [Theory]
        [InlineData(-18.9186, -48.2772, true)]  // Uberlandia
        [InlineData(-23.55, -46.63, true)]      // Sao Paulo
        [InlineData(40.7, -74.0, false)]        // Nova York
        [InlineData(0, 0, false)]
        [InlineData(double.NaN, -48.0, false)]
        [InlineData(-18.0, double.PositiveInfinity, false)]
        public void So_pontos_do_brasil_sao_aceitos(double lat, double lng, bool esperado) =>
            Assert.Equal(esperado, PontoDeEntregaRules.NoBrasil(lat, lng));

        [Fact]
        public void A_distancia_entre_dois_pontos_conhecidos_bate_com_a_realidade()
        {
            // Uberlandia -> Uberaba: cerca de 100 km em linha reta.
            var km = PontoDeEntregaRules.DistanciaKm(-18.9186, -48.2772, -19.7472, -47.9381);

            Assert.InRange(km, 90, 110);
            Assert.Equal(0, PontoDeEntregaRules.DistanciaKm(-18.9, -48.2, -18.9, -48.2), 6);
        }

        [Fact]
        public void A_validacao_cobre_ponto_ausente_origem_invalida_e_loja_sem_posicao()
        {
            Assert.NotNull(PontoDeEntregaRules.Validar(null, -48.0, "pino", null, null, null));
            Assert.NotNull(PontoDeEntregaRules.Validar(-18.9, null, "pino", null, null, null));
            Assert.NotNull(PontoDeEntregaRules.Validar(-18.9, -48.2, "xyz", null, null, null));
            Assert.Null(PontoDeEntregaRules.Validar(-18.9, -48.2, " PINO ", null, null, null));   // sem posicao da loja nao ha como medir
            Assert.Null(PontoDeEntregaRules.Validar(-18.9, -48.2, "gps", -18.9186, -48.2772, 8m));
            Assert.NotNull(PontoDeEntregaRules.Validar(-18.9, -48.2, "gps", -23.55, -46.63, 8m));
        }

        [Fact]
        public void O_teto_de_buscas_de_endereco_por_ip_devolve_429_depois_do_limite()
        {
            var servico = new Mock<ICardapioPublicService>();
            servico.Setup(s => s.LocalizarEnderecoAsync(It.IsAny<LocalizarCardapioEnderecoRequest>())).ReturnsAsync(new CardapioLocalizacaoDto());
            var cache = new MemoryCache(new MemoryCacheOptions());
            PublicCardapioController Build(string ip)
            {
                var contexto = new DefaultHttpContext();
                contexto.Connection.RemoteIpAddress = IPAddress.Parse(ip);
                return new PublicCardapioController(servico.Object, Mock.Of<ICardapioPedidoWebService>(), cache)
                {
                    ControllerContext = new ControllerContext { HttpContext = contexto }
                };
            }

            var resultados = new List<int?>();
            for (var i = 0; i < 41; i++)
            {
                var r = Build("203.0.113.9").LocalizarEndereco(new LocalizarCardapioEnderecoRequest()).GetAwaiter().GetResult();
                resultados.Add((r as ObjectResult)?.StatusCode ?? 200);
            }

            Assert.All(resultados.GetRange(0, 40), c => Assert.Equal(200, c));
            Assert.Equal(429, resultados[40]);

            // Outro IP nao e afetado.
            Assert.Equal(200, (Build("203.0.113.10").LocalizarEndereco(new LocalizarCardapioEnderecoRequest()).GetAwaiter().GetResult() as ObjectResult)?.StatusCode ?? 200);
        }
    }
}
