using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Merchants;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class IfoodMerchantProbeTests
    {
        private static readonly Guid Merchant = Guid.NewGuid();

        [Theory]
        [InlineData(HttpStatusCode.OK, true, "AUTHORIZED")]
        [InlineData(HttpStatusCode.Forbidden, false, "NOT_AUTHORIZED")]
        [InlineData(HttpStatusCode.NotFound, false, "NOT_AUTHORIZED")]
        [InlineData(HttpStatusCode.Unauthorized, false, "APP_CREDENTIALS")]
        [InlineData(HttpStatusCode.TooManyRequests, false, "RATE_LIMIT")]
        [InlineData(HttpStatusCode.InternalServerError, false, "UNAVAILABLE")]
        public async Task Resposta_do_iFood_vira_um_diagnostico_para_a_loja(HttpStatusCode status, bool ok, string codigo)
        {
            var probe = Montar(_ => new HttpResponseMessage(status), out var tokens);

            var resultado = await probe.CheckAsync(Merchant, CancellationToken.None);

            Assert.Equal(ok, resultado.Ok);
            Assert.Equal(codigo, resultado.Code);
            tokens.Verify(t => t.Invalidate(It.IsAny<IfoodAccessToken>()), status == HttpStatusCode.Unauthorized ? Times.Once() : Times.Never());
        }

        [Fact]
        public async Task Integracao_desligada_nao_chama_o_iFood()
        {
            var chamou = false;
            var probe = Montar(_ => { chamou = true; return new HttpResponseMessage(HttpStatusCode.OK); }, out _, enabled: false);

            var resultado = await probe.CheckAsync(Merchant, CancellationToken.None);

            Assert.Equal("DISABLED", resultado.Code);
            Assert.False(chamou);
        }

        [Fact]
        public async Task Merchant_vazio_e_recusado_sem_chamar_o_iFood()
        {
            var chamou = false;
            var probe = Montar(_ => { chamou = true; return new HttpResponseMessage(HttpStatusCode.OK); }, out _);

            var resultado = await probe.CheckAsync(Guid.Empty, CancellationToken.None);

            Assert.False(resultado.Ok);
            Assert.False(chamou);
        }

        [Fact]
        public async Task Rede_fora_vira_indisponivel_sem_estourar()
        {
            var probe = Montar(_ => throw new HttpRequestException("rede"), out _);

            var resultado = await probe.CheckAsync(Merchant, CancellationToken.None);

            Assert.Equal("UNAVAILABLE", resultado.Code);
        }

        [Fact]
        public async Task A_consulta_usa_o_token_do_app_e_o_id_do_merchant()
        {
            HttpRequestMessage? enviada = null;
            var probe = Montar(r => { enviada = r; return new HttpResponseMessage(HttpStatusCode.OK); }, out _);

            await probe.CheckAsync(Merchant, CancellationToken.None);

            Assert.EndsWith($"/merchant/v1.0/merchants/{Merchant:D}", enviada!.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", enviada.Headers.Authorization!.Scheme);
            Assert.Equal("token-do-app", enviada.Headers.Authorization.Parameter);
        }

        private static IfoodMerchantProbe Montar(Func<HttpRequestMessage, HttpResponseMessage> responder, out Mock<IIfoodTokenProvider> tokens, bool enabled = true)
        {
            tokens = new Mock<IIfoodTokenProvider>();
            tokens.Setup(t => t.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new IfoodAccessToken("token-do-app", DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromHours(1)));
            var http = new Mock<IHttpClientFactory>();
            http.Setup(h => h.CreateClient(IfoodMerchantProbe.ClientName)).Returns(() => new HttpClient(new Fake(responder)));
            var options = new Mock<IOptionsMonitor<IfoodOptions>>();
            options.Setup(o => o.CurrentValue).Returns(new IfoodOptions { Enabled = enabled });
            return new IfoodMerchantProbe(http.Object, tokens.Object, options.Object);
        }

        private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(responder(request));
        }
    }
}
