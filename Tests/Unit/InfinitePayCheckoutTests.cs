using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.InfinitePay.Checkout;
using APIBack.Payments.InfinitePay.Cliente;
using APIBack.Payments.MercadoPago;
using APIBack.Payments.Provedores;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class InfinitePayCheckoutTests
    {
        private static readonly DateTime Agora = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        private static readonly InfinitePayOptions Config = new() { WebhookUrl = "https://api.zippy.test/api/webhooks/infinitepay", MaxTentativas = 1 };

        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _respostas = new();
            public List<(HttpRequestMessage Req, string Corpo)> Chamadas { get; } = new();
            public FakeHandler Enfileirar(Func<HttpRequestMessage, HttpResponseMessage> r) { _respostas.Enqueue(r); return this; }
            public FakeHandler Enfileirar(HttpStatusCode s, string corpo) => Enfileirar(_ => new HttpResponseMessage(s) { Content = new StringContent(corpo, Encoding.UTF8, "application/json") });

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Chamadas.Add((request, request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(ct)));
                return _respostas.Dequeue()(request);
            }
        }

        private sealed class Fabrica : IHttpClientFactory
        {
            private readonly HttpMessageHandler _h;
            public Fabrica(HttpMessageHandler h) => _h = h;
            public HttpClient CreateClient(string name) => new(_h, false) { BaseAddress = new Uri("https://api.checkout.infinitepay.io/") };
        }

        private static InfinitePayClient Cliente(FakeHandler h, int tentativas = 1) =>
            new(new Fabrica(h), Microsoft.Extensions.Options.Options.Create(new InfinitePayOptions { MaxTentativas = tentativas }), NullLogger<InfinitePayClient>.Instance);

        private static InfinitePayCheckoutRequest Pedido(decimal valor = 25.90m) => new()
        {
            IdEstabelecimento = Guid.NewGuid(), PrePedidoId = Guid.NewGuid(), Valor = valor, Descricao = "Pedido web", IdempotencyKey = "cardapio:x:infinitepay",
            Itens = new List<InfinitePayItem>
            {
                new() { Description = "X-Burger", Price = 2090, Quantity = 1 },
                new() { Description = "Taxa de entrega", Price = 500, Quantity = 1 }
            },
            RedirectUrl = "https://loja.zippy.test/pedido/ok"
        };

        // ---- cliente HTTP ---------------------------------------------------------------------------------------------------------

        [Fact]
        public async Task EnviaChaveNoHeaderEMesmoCorpoSemExpor()
        {
            var h = new FakeHandler().Enfileirar(HttpStatusCode.OK, "{\"url\":\"https://checkout.infinitepay.io/loja/abc\"}");
            var r = await Cliente(h).CriarLinkAsync("{\"handle\":\"loja\"}", "inchk_segredo_123456");

            Assert.Equal("https://checkout.infinitepay.io/loja/abc", r.Url);
            var (req, corpo) = Assert.Single(h.Chamadas);
            Assert.Equal("links", req.RequestUri!.ToString().Split('/').Last());
            Assert.Equal("inchk_segredo_123456", req.Headers.GetValues("X-Api-Key").Single());
            Assert.DoesNotContain("inchk_", corpo);
            Assert.Equal("{\"handle\":\"loja\"}", corpo);
        }

        [Fact]
        public async Task SemChaveNaoEnviaHeader()
        {
            var h = new FakeHandler().Enfileirar(HttpStatusCode.OK, "{\"data\":{\"link\":\"https://checkout.infinitepay.io/x\"}}");
            await Cliente(h).CriarLinkAsync("{}", null);
            Assert.False(h.Chamadas[0].Req.Headers.Contains("X-Api-Key"));
        }

        [Theory]
        [InlineData(429)]
        [InlineData(503)]
        public async Task LimiteEIndisponibilidadeViramErroDeIndisponibilidade(int status)
        {
            var h = new FakeHandler().Enfileirar((HttpStatusCode)status, "{}");
            var ex = await Assert.ThrowsAsync<InfinitePayApiException>(() => Cliente(h).CriarLinkAsync("{}", null));
            Assert.True(ex.Indisponivel);
            Assert.False(ex.RecusaDefinitiva);
            Assert.Equal(InfinitePayCodigos.Indisponivel, ex.ParaPagamento().Codigo);
        }

        [Fact]
        public async Task RepeteComOMesmoCorpoEmFalhaTransitoria()
        {
            var h = new FakeHandler().Enfileirar(HttpStatusCode.BadGateway, "{}").Enfileirar(HttpStatusCode.OK, "{\"url\":\"https://checkout.infinitepay.io/ok\"}");
            var r = await Cliente(h, 2).CriarLinkAsync("{\"order_nsu\":\"n1\"}", null);

            Assert.Equal("https://checkout.infinitepay.io/ok", r.Url);
            Assert.Equal(2, h.Chamadas.Count);
            Assert.Equal(h.Chamadas[0].Corpo, h.Chamadas[1].Corpo);
        }

        [Fact]
        public async Task Http403ViraContaIncompativelSemRepetir()
        {
            var h = new FakeHandler().Enfileirar(HttpStatusCode.Forbidden, "{\"error\":\"forbidden\"}");
            var ex = await Assert.ThrowsAsync<InfinitePayApiException>(() => Cliente(h, 2).CriarLinkAsync("{}", "inchk_outra_conta_123"));
            Assert.Single(h.Chamadas);
            Assert.True(ex.RecusaDefinitiva);
            Assert.Equal(InfinitePayCodigos.ContaIncompativel, ex.ParaPagamento().Codigo);
        }

        [Fact]
        public async Task CheckoutNaoHabilitadoTemCodigoProprio()
        {
            var h = new FakeHandler().Enfileirar(HttpStatusCode.UnprocessableEntity, "{\"error\":\"external_checkout_not_enabled\"}");
            var ex = await Assert.ThrowsAsync<InfinitePayApiException>(() => Cliente(h).CriarLinkAsync("{}", null));
            Assert.Equal(InfinitePayCodigos.CheckoutDesabilitado, ex.ParaPagamento().Codigo);
        }

        [Fact]
        public async Task TimeoutViraIndisponivelSemResposta()
        {
            var h = new FakeHandler().Enfileirar(_ => throw new TaskCanceledException("timeout"));
            var ex = await Assert.ThrowsAsync<InfinitePayApiException>(() => Cliente(h).CriarLinkAsync("{}", null));
            Assert.Equal(0, ex.StatusHttp);
            Assert.True(ex.Indisponivel);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("{}")]
        [InlineData("{\"url\":\"http://inseguro.test/x\"}")]
        [InlineData("{\"url\":\"javascript:alert(1)\"}")]
        [InlineData("[]")]
        public async Task RespostaInvalidaOuSemHttpsNaoViraLink(string corpo)
        {
            var h = new FakeHandler().Enfileirar(HttpStatusCode.OK, corpo);
            var ex = await Assert.ThrowsAsync<InfinitePayApiException>(() => Cliente(h).CriarLinkAsync("{}", null));
            Assert.Equal(InfinitePayCodigos.RespostaInconsistente, ex.ParaPagamento().Codigo);
        }

        // ---- montagem do corpo ----------------------------------------------------------------------------------------------------

        [Fact]
        public void SemChaveSoTemCapacidadesBasicasEHandleNoCorpo()
        {
            var (corpo, expira) = InfinitePayCheckoutMontagem.Montar(Pedido(), "lojadoze", "zp_p_cardapio_abc", false, Config, Agora);
            var json = JsonDocument.Parse(InfinitePayClient.Serializar(corpo)).RootElement;

            Assert.Equal("lojadoze", json.GetProperty("handle").GetString());
            Assert.False(json.TryGetProperty("options", out _));
            Assert.Null(expira);
            Assert.Equal("https://api.zippy.test/api/webhooks/infinitepay", json.GetProperty("webhook_url").GetString());
            Assert.Equal("zp_p_cardapio_abc", json.GetProperty("order_nsu").GetString());
        }

        [Fact]
        public void SemChaveRecusaOpcaoAvancadaEmVezDeIgnorar()
        {
            var r = Pedido();
            r.Opcoes = new InfinitePayOpcoesSolicitadas { DescontoPixPercentual = 5 };
            var ex = Assert.Throws<PagamentoException>(() => InfinitePayCheckoutMontagem.Montar(r, "loja", "n", false, Config, Agora));
            Assert.Equal(ProvedorPagamentoCodigos.NaoSuportado, ex.Codigo);
        }

        [Fact]
        public void ComChaveUsaPagamentoUnicoValidadeEMetodosPadrao()
        {
            var (corpo, expira) = InfinitePayCheckoutMontagem.Montar(Pedido(), "loja", "n", true, Config, Agora);
            var o = corpo.Options!;

            Assert.True(o.SinglePayment);
            Assert.Equal(new[] { "credit_card", "pix" }, o.PaymentMethodsEnabled);
            Assert.Null(o.Discounts);
            Assert.Equal(Agora.AddDays(1), expira);
            Assert.Equal("2026-11-06T12:00:00Z", o.ValidUntil);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(13)]
        public void ParcelasForaDe0a12SaoRecusadas(int parcelas)
        {
            var r = Pedido();
            r.Opcoes = new InfinitePayOpcoesSolicitadas { ParcelasAssumidas = parcelas };
            Assert.Throws<PagamentoException>(() => InfinitePayCheckoutMontagem.Montar(r, "loja", "n", true, Config, Agora));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(100)]
        public void DescontoPixPrecisaEstarEntre0e100Exclusivo(decimal pct)
        {
            var r = Pedido();
            r.Opcoes = new InfinitePayOpcoesSolicitadas { DescontoPixPercentual = pct };
            Assert.Throws<PagamentoException>(() => InfinitePayCheckoutMontagem.Montar(r, "loja", "n", true, Config, Agora));
        }

        [Fact]
        public void ValidadeAcimaDeUmAnoERecusada()
        {
            var r = Pedido();
            r.Opcoes = new InfinitePayOpcoesSolicitadas { ValidadeMinutos = 366 * 24 * 60 };
            Assert.Throws<PagamentoException>(() => InfinitePayCheckoutMontagem.Montar(r, "loja", "n", true, Config, Agora));
        }

        [Fact]
        public void WebhookERedirectPrecisamSerHttpsEHostPermitido()
        {
            var semHttps = new InfinitePayOptions { WebhookUrl = "http://api.zippy.test/w" };
            Assert.Throws<PagamentoException>(() => InfinitePayCheckoutMontagem.Montar(Pedido(), "loja", "n", false, semHttps, Agora));

            var r = Pedido();
            r.RedirectUrl = "https://evil.test/x";
            var restrito = new InfinitePayOptions { WebhookUrl = Config.WebhookUrl, RedirectHostsPermitidos = new List<string> { "loja.zippy.test" } };
            Assert.Throws<PagamentoException>(() => InfinitePayCheckoutMontagem.Montar(r, "loja", "n", false, restrito, Agora));
        }

        // ---- valores --------------------------------------------------------------------------------------------------------------

        [Fact]
        public void CentavosSaoInteirosEOsItensPrecisamFecharOTotal()
        {
            Assert.Equal(2590, InfinitePayRegras.Centavos(25.90m));
            Assert.Equal(1999, InfinitePayRegras.Centavos(19.99m));
            Assert.Throws<PagamentoException>(() => InfinitePayRegras.Centavos(10.005m));

            InfinitePayCheckoutMontagem.ValidarPedido(Pedido(25.90m));
            var ex = Assert.Throws<PagamentoException>(() => InfinitePayCheckoutMontagem.ValidarPedido(Pedido(25.91m)));
            Assert.Equal(PagamentoCodigos.ValorInvalido, ex.Codigo);
        }

        [Fact]
        public void ItemAbaixoDeUmRealEmQuantidadeInvalidaSaoRecusados()
        {
            var r = Pedido(0.99m);
            r.Itens = new List<InfinitePayItem> { new() { Description = "Brinde", Price = 99, Quantity = 1 } };
            Assert.Throws<PagamentoException>(() => InfinitePayCheckoutMontagem.ValidarPedido(r));

            var q = Pedido();
            q.Itens[0].Quantity = 0;
            Assert.Throws<PagamentoException>(() => InfinitePayCheckoutMontagem.ValidarPedido(q));
        }

        [Fact]
        public void CartaoSempreAtivoENuncaAnunciaSoPix()
        {
            Assert.Equal(new[] { "pix", "credit_card" }, InfinitePayRegras.MetodosEfetivos(null));
            Assert.Equal(new[] { "pix", "credit_card" }, InfinitePayRegras.MetodosEfetivos(new InfinitePayOpcoes { PaymentMethodsEnabled = new List<string> { "pix" } }));
            Assert.Equal(new[] { "credit_card" }, InfinitePayRegras.MetodosEfetivos(new InfinitePayOpcoes { PaymentMethodsEnabled = new List<string> { "credit_card" } }));
        }

        [Fact]
        public void CorpoNaoCarregaTelefoneNemDadoPessoalNoOrderNsu()
        {
            var id = Guid.NewGuid();
            var nsu = PagamentoIntentSql.ExternalReference(Ambientes.Producao, OrigensPagamento.Cardapio, id);
            Assert.Equal($"zp_p_cardapio_{id:N}", nsu);
            Assert.True(nsu.Length <= 64);
        }
    }
}
