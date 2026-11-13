using System;
using APIBack.Payments.Chat;
using APIBack.Payments.InfinitePay.Checkout;
using APIBack.Payments.InfinitePay.Cliente;
using APIBack.Payments.InfinitePay.Webhooks;
using APIBack.Payments.MercadoPago;
using Xunit;

namespace APIBack.Tests.Unit
{
    /// <summary>Prompt 05, modalidade "Link de pagamento (Pix ou cartao)" no chat. Regras puras: sem banco nem rede.</summary>
    public sealed class CobrancaChatLinkTests
    {
        [Theory]
        [InlineData(null, "pix")]
        [InlineData("", "pix")]
        [InlineData("PIX", "pix")]
        [InlineData("link", "link")]
        [InlineData(" Link ", "link")]
        public void Modalidade_padrao_e_pix_e_link_so_por_escolha_explicita(string? entrada, string esperado) =>
            Assert.Equal(esperado, CobrancaChatRegras.ModalidadeDe(entrada));

        [Fact]
        public void Modalidade_desconhecida_e_recusada_sem_cair_em_pix()
        {
            var ex = Assert.Throws<PagamentoException>(() => CobrancaChatRegras.ModalidadeDe("cartao"));
            Assert.Equal(400, ex.Http);
        }

        [Fact]
        public void Rotulos_deixam_claro_que_o_link_nao_e_pix()
        {
            Assert.Equal("Pix", CobrancaChatRegras.RotuloModalidade(ModalidadesCobranca.Pix));
            Assert.Equal("Link de pagamento (Pix ou cartão)", CobrancaChatRegras.RotuloModalidade(ModalidadesCobranca.Link));
            Assert.Contains("Não é Pix exclusivo", ModalidadesCobranca.AvisoLink);
            Assert.Contains("reembolso", ModalidadesCobranca.AvisoLink);
        }

        [Fact]
        public void Texto_ao_cliente_do_link_fala_em_pix_ou_cartao_e_nunca_em_pix_exclusivo()
        {
            var texto = CobrancaChatRegras.TextoLink("Pedido 12", 45.5m, null, "https://checkout.infinitepay.io/abc");
            Assert.Contains("Pix ou cartao", texto);
            Assert.Contains("https://checkout.infinitepay.io/abc", texto);
            Assert.DoesNotContain("Pague com Pix", texto);
            Assert.DoesNotContain("copia", texto, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Texto_do_link_sem_url_nao_e_enviado() =>
            Assert.Throws<PagamentoException>(() => CobrancaChatRegras.TextoLink("x", 10m, null, ""));

        [Fact]
        public void Texto_do_pix_continua_como_antes()
        {
            var textos = CobrancaChatRegras.Textos("link", "Pedido 12", 45.5m, null, "https://zippy.test/pix/t", null);
            Assert.Contains("Pague com Pix pelo link", textos[0]);
        }

        private static InfinitePayCheckoutRequest Chat(Guid? conversa) => new()
        {
            IdEstabelecimento = Guid.NewGuid(), Origem = OrigensPagamento.Chat, ConversaId = conversa, Valor = 20m, Descricao = "Cobranca", IdempotencyKey = "chat:abc",
            Itens = { new InfinitePayItem { Description = "Cobranca", Price = 2000, Quantity = 1 } }
        };

        [Fact]
        public void Checkout_do_chat_avulso_nao_exige_pre_pedido_mas_exige_conversa()
        {
            InfinitePayCheckoutMontagem.ValidarPedido(Chat(Guid.NewGuid()));
            Assert.Throws<PagamentoException>(() => InfinitePayCheckoutMontagem.ValidarPedido(Chat(null)));
        }

        [Fact]
        public void Checkout_do_cardapio_continua_exigindo_pre_pedido()
        {
            var r = Chat(Guid.NewGuid());
            r.Origem = OrigensPagamento.Cardapio;
            Assert.Throws<PagamentoException>(() => InfinitePayCheckoutMontagem.ValidarPedido(r));
        }

        [Fact]
        public void Checkout_so_aceita_origem_cardapio_ou_chat()
        {
            var r = Chat(Guid.NewGuid());
            r.Origem = OrigensPagamento.Assinatura;
            Assert.Throws<PagamentoException>(() => InfinitePayCheckoutMontagem.ValidarPedido(r));
        }

        [Theory]
        [InlineData("zp_p_chat_0123456789abcdef0123456789abcdef", true)]
        [InlineData("zp_p_cardapio_0123456789abcdef0123456789abcdef", true)]
        [InlineData("zp_s_chat_0123456789abcdef0123456789abcdef", false)]
        [InlineData("zp_p_assinatura_0123456789abcdef0123456789abcdef", false)]
        public void Webhook_reconhece_order_nsu_do_chat(string orderNsu, bool valido)
        {
            var corpo = "{\"order_nsu\":\"" + orderNsu + "\",\"transaction_nsu\":\"tx1\",\"invoice_slug\":\"s1\"}";
            Assert.Equal(valido, InfinitePayWebhookRegras.Validar(corpo, out _) != null);
        }
    }
}
