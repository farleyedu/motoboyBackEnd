using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.InfinitePay.Cliente;
using APIBack.Payments.InfinitePay.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class InfinitePayWebhookTests
    {
        private const string Nsu = "zp_p_cardapio_0123456789abcdef0123456789abcdef";

        private static string Corpo(object extra) => JsonSerializer.Serialize(extra);

        private static string Pago() => Corpo(new
        {
            invoice_slug = "abc123", amount = 2590, paid_amount = 2640, installments = 1, capture_method = "credit_card",
            transaction_nsu = "5f0c1d2e-aaaa-bbbb-cccc-0123456789ab", order_nsu = Nsu, receipt_url = "https://comprovante.test/1"
        });

        // ---- entrada nao confiavel ------------------------------------------------------------------------------------------------

        [Fact]
        public void AvisoValidoVirapayloadSemAfirmarPagamento()
        {
            var p = InfinitePayWebhookRegras.Validar(Pago(), out var motivo);

            Assert.NotNull(p);
            Assert.Null(motivo);
            Assert.Equal(Nsu, p!.OrderNsu);
            Assert.Equal("abc123", p.InvoiceSlug);
            Assert.Null(p.TipoEvento);
            // O payload nao tem campo "pago": o aviso so escolhe o que verificar.
            Assert.DoesNotContain(typeof(InfinitePayWebhookPayload).GetProperties(), x => x.Name.Equals("Paid", StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [InlineData("")]
        [InlineData("nao e json")]
        [InlineData("[]")]
        [InlineData("{\"order_nsu\":\"123456\"}")]                                              // order_nsu que nao e nosso
        [InlineData("{\"order_nsu\":\"zp_s_cardapio_0123456789abcdef0123456789abcdef\"}")]      // sandbox nao existe na InfinitePay
        [InlineData("{\"order_nsu\":\"zp_p_cardapio_0123456789abcdef0123456789abcdef\",\"transaction_nsu\":\"x y'--\"}")]
        [InlineData("{\"order_nsu\":\"zp_p_cardapio_0123456789abcdef0123456789abcdef\",\"amount\":-1}")]
        [InlineData("{\"order_nsu\":\"zp_p_cardapio_0123456789abcdef0123456789abcdef\",\"amount\":\"10\"}")]
        [InlineData("{\"order_nsu\":\"zp_p_cardapio_0123456789abcdef0123456789abcdef\",\"capture_method\":\"boleto\"}")]
        public void WebhookForjadoOuMalFormadoERecusado(string corpo)
        {
            Assert.Null(InfinitePayWebhookRegras.Validar(corpo, out var motivo));
            Assert.NotNull(motivo);
        }

        [Fact]
        public void CorpoAcimaDoLimiteERecusado()
        {
            var grande = Corpo(new { order_nsu = Nsu, items = new string('x', InfinitePayWebhookRegras.CorpoMaximoBytes) });
            Assert.Null(InfinitePayWebhookRegras.Validar(grande, out _));
        }

        [Fact]
        public void MesmoAvisoTemMesmoHashEOutroTransactionTemOutro()
        {
            var a = InfinitePayWebhookRegras.Validar(Pago(), out _)!;
            var b = InfinitePayWebhookRegras.Validar(Pago(), out _)!;
            var c = InfinitePayWebhookRegras.Validar(Pago().Replace("5f0c1d2e", "00000000"), out _)!;

            Assert.Equal(a.HashDeduplicacao(), b.HashDeduplicacao());
            Assert.NotEqual(a.HashDeduplicacao(), c.HashDeduplicacao());
        }

        // ---- o que o aviso pode disparar ------------------------------------------------------------------------------------------

        [Theory]
        [InlineData("paid", "tx", "slug", AcaoEvento.VerificarPagamento)]
        [InlineData("settled", "tx", "slug", AcaoEvento.VerificarPagamento)]
        [InlineData(null, "tx", "slug", AcaoEvento.VerificarPagamento)]   // exemplo oficial de "pago" nao tem campo de tipo
        [InlineData("paid", null, "slug", AcaoEvento.PendenciaSemIdentificadores)]
        [InlineData("paid", "tx", null, AcaoEvento.PendenciaSemIdentificadores)]
        [InlineData(null, null, null, AcaoEvento.Ignorar)]
        [InlineData("created", null, null, AcaoEvento.Ignorar)]
        [InlineData("payment_failed", "tx", "slug", AcaoEvento.Ignorar)]  // falha de uma tentativa nao encerra a cobranca
        [InlineData("canceled", "tx", "slug", AcaoEvento.PendenciaCancelamento)]
        [InlineData("refunded", "tx", "slug", AcaoEvento.PendenciaEstorno)]
        public void ClassificacaoNuncaTrataCancelEstornoComoVerdade(string? tipo, string? tx, string? slug, AcaoEvento esperado) =>
            Assert.Equal(esperado, InfinitePayWebhookRegras.Classificar(tipo, tx, slug));

        [Fact]
        public void EstornoENuncaDisparaConfirmacaoNemReversaoAutomatica()
        {
            // pagar + estornar concorrentes: refunded so abre pendencia; paid segue verificado de forma independente.
            Assert.NotEqual(AcaoEvento.VerificarPagamento, InfinitePayWebhookRegras.Classificar("refunded", "tx", "slug"));
            Assert.Equal(AcaoEvento.VerificarPagamento, InfinitePayWebhookRegras.Classificar("paid", "tx", "slug"));
        }

        // ---- conferencia do /payment_check ----------------------------------------------------------------------------------------

        [Fact]
        public void ConfirmaPeloAmountOriginalEPaidAmountPodeSerMaior()
        {
            var ok = new InfinitePayPaymentCheck { Success = true, Paid = true, Amount = 2590, PaidAmount = 2640 };
            Assert.Equal(AvaliacaoPagamento.Confirmado, InfinitePayWebhookRegras.Avaliar(ok, 2590));
        }

        [Fact]
        public void AmountDiferenteDoCobradoViraDivergenciaMesmoComPaidAmountIgual()
        {
            var c = new InfinitePayPaymentCheck { Success = true, Paid = true, Amount = 100, PaidAmount = 2590 };
            Assert.Equal(AvaliacaoPagamento.ValorDivergente, InfinitePayWebhookRegras.Avaliar(c, 2590));
            Assert.Equal(AvaliacaoPagamento.ValorDivergente, InfinitePayWebhookRegras.Avaliar(new InfinitePayPaymentCheck { Success = true, Paid = true }, 2590));
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(false, false)]
        public void Http200ComSuccessOuPaidFalsoNaoConfirma(bool success, bool paid) =>
            Assert.Equal(AvaliacaoPagamento.NaoConfirmado,
                InfinitePayWebhookRegras.Avaliar(new InfinitePayPaymentCheck { Success = success, Paid = paid, Amount = 2590 }, 2590));

        [Fact]
        public void LeituraDoPaymentCheckEstritaECapturaSoMetodosConhecidos()
        {
            var r = InfinitePayPaymentCheck.Ler("{\"success\":true,\"paid\":true,\"amount\":1500,\"paid_amount\":1510,\"capture_method\":\"pix\"}")!;
            Assert.True(r.Success && r.Paid);
            Assert.Equal(1500, r.Amount);
            Assert.Equal(1510, r.PaidAmount);
            Assert.Equal("pix", r.CaptureMethod);

            var texto = InfinitePayPaymentCheck.Ler("{\"success\":\"true\",\"paid\":1,\"capture_method\":\"boleto\"}")!;
            Assert.False(texto.Success);
            Assert.False(texto.Paid);
            Assert.Null(texto.CaptureMethod);
            Assert.Null(InfinitePayPaymentCheck.Ler("lixo"));
        }

        // ---- recuperacao ----------------------------------------------------------------------------------------------------------

        [Fact]
        public void BackoffDobraECrescePorUmaHora()
        {
            Assert.Equal(TimeSpan.FromSeconds(30), InfinitePayWebhookRegras.Backoff(1));
            Assert.Equal(TimeSpan.FromSeconds(60), InfinitePayWebhookRegras.Backoff(2));
            Assert.Equal(TimeSpan.FromSeconds(240), InfinitePayWebhookRegras.Backoff(4));
            Assert.Equal(TimeSpan.FromHours(1), InfinitePayWebhookRegras.Backoff(50));
        }

        // ---- cliente /payment_check -----------------------------------------------------------------------------------------------

        private sealed class Handler : HttpMessageHandler
        {
            private readonly Queue<Func<HttpResponseMessage>> _fila = new();
            public List<(HttpRequestMessage Req, string Corpo)> Chamadas { get; } = new();
            public Handler Enfileirar(HttpStatusCode s, string corpo) { _fila.Enqueue(() => new HttpResponseMessage(s) { Content = new StringContent(corpo, Encoding.UTF8, "application/json") }); return this; }
            public Handler Falhar() { _fila.Enqueue(() => throw new TaskCanceledException("timeout")); return this; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Chamadas.Add((request, request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(ct)));
                return _fila.Dequeue()();
            }
        }

        private sealed class Fabrica : IHttpClientFactory
        {
            private readonly HttpMessageHandler _h;
            public Fabrica(HttpMessageHandler h) => _h = h;
            public HttpClient CreateClient(string name) => new(_h, false) { BaseAddress = new Uri("https://api.checkout.infinitepay.io/") };
        }

        private static InfinitePayClient Cliente(Handler h, int tentativas = 1) =>
            new(new Fabrica(h), Microsoft.Extensions.Options.Options.Create(new InfinitePayOptions { MaxTentativas = tentativas }), NullLogger<InfinitePayClient>.Instance);

        [Fact]
        public async Task PaymentCheckEnviaOsQuatroCamposDaContaGravadaEChaveSoNoHeader()
        {
            var h = new Handler().Enfileirar(HttpStatusCode.OK, "{\"success\":true,\"paid\":true,\"amount\":2590,\"paid_amount\":2640,\"capture_method\":\"credit_card\"}");
            var r = await Cliente(h).VerificarPagamentoAsync("lojadoze", Nsu, "tx-1", "slug-1", "inchk_segredo_123456");

            Assert.True(r.Paid);
            var (req, corpo) = Assert.Single(h.Chamadas);
            Assert.EndsWith("/payment_check", req.RequestUri!.AbsolutePath);
            Assert.Equal("inchk_segredo_123456", req.Headers.GetValues("X-Api-Key").Single());
            Assert.DoesNotContain("inchk_", corpo);
            var json = JsonDocument.Parse(corpo).RootElement;
            Assert.Equal("lojadoze", json.GetProperty("handle").GetString());
            Assert.Equal(Nsu, json.GetProperty("order_nsu").GetString());
            Assert.Equal("tx-1", json.GetProperty("transaction_nsu").GetString());
            Assert.Equal("slug-1", json.GetProperty("slug").GetString());
        }

        [Fact]
        public async Task PaymentCheckSemIdentificadoresNemChegaNaRede()
        {
            var h = new Handler();
            await Assert.ThrowsAsync<ArgumentException>(() => Cliente(h).VerificarPagamentoAsync("loja", Nsu, "", "slug", null));
            await Assert.ThrowsAsync<ArgumentException>(() => Cliente(h).VerificarPagamentoAsync("loja", Nsu, "tx", " ", null));
            Assert.Empty(h.Chamadas);
        }

        [Fact]
        public async Task PaymentCheckRepeteEmFalhaExternaEDevolveStatusFalsoSemErro()
        {
            var h = new Handler().Falhar().Enfileirar(HttpStatusCode.OK, "{\"success\":false,\"paid\":false}");
            var r = await Cliente(h, 2).VerificarPagamentoAsync("loja", Nsu, "tx", "slug", null);

            Assert.False(r.Success);
            Assert.Equal(2, h.Chamadas.Count);
            Assert.False(h.Chamadas[0].Req.Headers.Contains("X-Api-Key"));
        }

        [Fact]
        public async Task PaymentCheckTimeoutEsgotadoViraIndisponivel()
        {
            var h = new Handler().Falhar();
            var ex = await Assert.ThrowsAsync<InfinitePayApiException>(() => Cliente(h).VerificarPagamentoAsync("loja", Nsu, "tx", "slug", null));
            Assert.True(ex.Indisponivel);
        }

        [Fact]
        public async Task PaymentCheck403ESemRepeticaoERecusaDefinitiva()
        {
            var h = new Handler().Enfileirar(HttpStatusCode.Forbidden, "{}");
            var ex = await Assert.ThrowsAsync<InfinitePayApiException>(() => Cliente(h, 2).VerificarPagamentoAsync("loja", Nsu, "tx", "slug", "inchk_outra_conta_12"));
            Assert.Single(h.Chamadas);
            Assert.True(ex.RecusaDefinitiva);
        }
    }
}
