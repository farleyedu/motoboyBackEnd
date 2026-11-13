using System;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.InfinitePay.Cardapio;
using APIBack.Payments.MercadoPago;
using APIBack.Payments.Provedores;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    /// <summary>Prompt 05: preferencia do provedor no cardapio, chaves do link hospedado e meio "online". Sem banco nem rede.</summary>
    public sealed class CardapioCheckoutHospedadoTests
    {
        private sealed class Preferencias : IPagamentoProvedorPreferenciaResolver
        {
            private readonly PreferenciaProvedorResolvida _checkout;
            private readonly PreferenciaProvedorResolvida _pix;

            public Preferencias(bool checkoutHabilitado, string? checkout, bool pixHabilitado, string? pix)
            {
                _checkout = new PreferenciaProvedorResolvida(FluxosPagamento.CheckoutOnline, checkoutHabilitado, checkout, false);
                _pix = new PreferenciaProvedorResolvida(FluxosPagamento.PixExclusivo, pixHabilitado, pix, false);
            }

            public Task<PreferenciaProvedorResolvida> ResolverAsync(Guid loja, string fluxo, CancellationToken ct = default) =>
                Task.FromResult(fluxo == FluxosPagamento.CheckoutOnline ? _checkout : _pix);
        }

        // Os caminhos testados aqui nunca tocam banco, cliente HTTP nem sincronizador.
        private static CardapioCheckoutHospedadoService Servico(Preferencias p) => new(null!, p, null!, null!, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        private static BrickConfiguracao Mp() => new() { PixAtivo = true, CartaoAtivo = true, PublicKey = "PK", ParcelasMax = 3, ContaConectada = true, PresencialAtivo = true };

        [Fact]
        public async Task Mercado_pago_no_checkout_mantem_pix_e_brick_sem_link()
        {
            var s = Servico(new Preferencias(true, ProvedoresPagamento.MercadoPago, false, null));
            var c = await s.AjustarConfiguracaoAsync(Guid.NewGuid(), Mp());
            Assert.True(c.PixAtivo);
            Assert.True(c.CartaoAtivo);
            Assert.Equal("PK", c.PublicKey);
            Assert.False(c.CheckoutHospedado);
            Assert.Equal(ProvedoresPagamento.MercadoPago, c.ProvedorCheckout);
            Assert.True(await s.PermiteNovoMercadoPagoAsync(Guid.NewGuid(), "pix"));
            Assert.True(await s.PermiteNovoMercadoPagoAsync(Guid.NewGuid(), "cartao"));
        }

        [Fact]
        public async Task Checkout_desabilitado_nao_cai_para_outro_provedor()
        {
            var s = Servico(new Preferencias(false, null, false, null));
            var c = await s.AjustarConfiguracaoAsync(Guid.NewGuid(), Mp());
            Assert.False(c.PixAtivo);
            Assert.False(c.CartaoAtivo);
            Assert.Null(c.PublicKey);
            Assert.False(c.CheckoutHospedado);
            Assert.Null(c.ProvedorCheckout);
            Assert.False(await s.PermiteNovoMercadoPagoAsync(Guid.NewGuid(), "pix"));
            Assert.False(await s.PermiteNovoMercadoPagoAsync(Guid.NewGuid(), "cartao"));
        }

        [Fact]
        public async Task Infinitepay_no_checkout_so_permite_pix_nativo_se_a_loja_escolheu_pix_exclusivo_no_mp()
        {
            var comPix = Servico(new Preferencias(true, ProvedoresPagamento.InfinitePay, true, ProvedoresPagamento.MercadoPago));
            Assert.True(await comPix.PermiteNovoMercadoPagoAsync(Guid.NewGuid(), "pix"));
            Assert.False(await comPix.PermiteNovoMercadoPagoAsync(Guid.NewGuid(), "cartao"));

            var semPix = Servico(new Preferencias(true, ProvedoresPagamento.InfinitePay, false, null));
            Assert.False(await semPix.PermiteNovoMercadoPagoAsync(Guid.NewGuid(), "pix"));
            Assert.False(await semPix.PermiteNovoMercadoPagoAsync(Guid.NewGuid(), "cartao"));
        }

        [Fact]
        public async Task Link_novo_exige_infinitepay_escolhida()
        {
            var s = Servico(new Preferencias(true, ProvedoresPagamento.MercadoPago, false, null));
            var ex = await Assert.ThrowsAsync<PagamentoException>(() => s.ExigirParaNovoLinkAsync(Guid.NewGuid()));
            Assert.Equal(ProvedorPagamentoCodigos.ProvedorNaoConfigurado, ex.Codigo);
            var off = Servico(new Preferencias(false, null, false, null));
            await Assert.ThrowsAsync<PagamentoException>(() => off.ExigirParaNovoLinkAsync(Guid.NewGuid()));
        }

        [Fact]
        public void Chaves_do_link_nao_se_misturam_com_pix_nem_cartao()
        {
            var id = Guid.NewGuid();
            Assert.Equal($"cardapio:{id:N}:infinitepay", CardapioPagamentoRules.ChaveInfinitePay(id, 1));
            Assert.Equal($"cardapio:{id:N}:infinitepay:2", CardapioPagamentoRules.ChaveInfinitePay(id, 2));
            Assert.True(CardapioPagamentoRules.EhChaveInfinitePay(CardapioPagamentoRules.ChaveInfinitePay(id, 3)));
            Assert.False(CardapioPagamentoRules.EhChaveCartao(CardapioPagamentoRules.ChaveInfinitePay(id, 1)));
            Assert.False(CardapioPagamentoRules.EhChaveInfinitePay(CardapioPagamentoRules.ChavePix(id, 1)));
            Assert.False(CardapioPagamentoRules.EhChaveInfinitePay(CardapioPagamentoRules.ChaveCartao(id, 2)));
        }

        [Theory]
        [InlineData("Pagar online (Pix ou cartão)", "online")]
        [InlineData("pix_online", "pix")]
        [InlineData("Cartão online", "cartao")]
        [InlineData("link_online", "online")]
        [InlineData("Pix na entrega", null)]
        public void Meio_online_do_pedido(string forma, string? esperado) => Assert.Equal(esperado, CardapioPagamentoRules.MetodoOnline(forma));

        [Fact]
        public void Texto_do_pedido_nao_promete_pix_quando_foi_link()
        {
            Assert.Equal("Pago online - Pix ou cartao", CardapioPagamentoRules.FormaPagamentoDoPedido("online"));
            Assert.Equal("Pago online - Pix", CardapioPagamentoRules.FormaPagamentoDoPedido("pix"));
            Assert.Equal("Pago online - cartao", CardapioPagamentoRules.FormaPagamentoDoPedido("cartao"));
        }
    }
}
