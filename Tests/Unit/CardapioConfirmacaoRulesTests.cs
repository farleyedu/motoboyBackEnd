using System;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class CardapioConfirmacaoCodigoTests
    {
        [Theory]
        [InlineData(1000, "1000")]
        [InlineData(4821, "4821")]
        [InlineData(9999, "9999")]
        public void Code_is_four_digits_from_the_injected_draw(int sorteado, string esperado)
        {
            int? minimoVisto = null;
            int? maximoVisto = null;
            var codigo = CardapioConfirmacaoRules.GerarCodigo((minimo, maximo) =>
            {
                minimoVisto = minimo;
                maximoVisto = maximo;
                return sorteado;
            });

            Assert.Equal(esperado, codigo);
            // Intervalo [1000, 10000): nunca zero a esquerda.
            Assert.Equal(1000, minimoVisto);
            Assert.Equal(10000, maximoVisto);
        }

        [Fact]
        public void Default_draw_always_yields_four_digits_without_leading_zero()
        {
            for (var i = 0; i < 300; i++)
            {
                Assert.Matches(@"^[1-9]\d{3}$", CardapioConfirmacaoRules.GerarCodigo());
            }
        }

        [Theory]
        [InlineData("4821", "4821", false)]
        [InlineData("  4821  ", "4821", false)]
        [InlineData("4821.", "4821", false)]
        [InlineData("Código: 4821", "4821", true)]
        [InlineData("codigo 4821", "4821", true)]
        [InlineData("CÓDIGO #4821", "4821", true)]
        [InlineData("Olá! Quero confirmar meu pedido. Código: 4821", "4821", true)]
        public void Extracts_the_code_from_a_short_message(string texto, string codigo, bool explicito)
        {
            var extraido = CardapioConfirmacaoRules.ExtrairCodigo(texto);

            Assert.NotNull(extraido);
            Assert.Equal(codigo, extraido!.Value.Codigo);
            Assert.Equal(explicito, extraido.Value.Explicito);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("oi")]
        [InlineData("12345")]
        [InlineData("123")]
        [InlineData("Código: 48215")]
        [InlineData("quero 2 pizzas até 1234")]
        [InlineData("moro na rua 1234 bloco 5")]
        public void Ignores_messages_that_are_not_a_code(string? texto)
        {
            Assert.Null(CardapioConfirmacaoRules.ExtrairCodigo(texto));
        }

        [Fact]
        public void Ignores_long_messages_even_with_the_word_and_digits()
        {
            var longo = "Olá, gostaria de saber o código da promoção 4821 " + new string('x', 200);

            Assert.Null(CardapioConfirmacaoRules.ExtrairCodigo(longo));
        }
    }

    public class CardapioConfirmacaoTelefoneTests
    {
        [Fact]
        public void Mobile_with_ninth_digit_also_yields_the_variant_without_it()
        {
            var variantes = CardapioConfirmacaoRules.VariantesTelefone("(34) 99123-0001");

            Assert.Equal(new[] { "5534991230001", "553491230001" }, variantes);
        }

        [Fact]
        public void Old_mobile_without_ninth_digit_also_yields_the_variant_with_it()
        {
            var variantes = CardapioConfirmacaoRules.VariantesTelefone("+55 34 9123-0001");

            Assert.Equal(new[] { "553491230001", "5534991230001" }, variantes);
        }

        [Fact]
        public void Landline_has_a_single_variant()
        {
            Assert.Equal(new[] { "553432123456" }, CardapioConfirmacaoRules.VariantesTelefone("(34) 3212-3456"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("12345")]
        [InlineData("abc")]
        public void Invalid_phone_has_no_variants(string? telefone)
        {
            Assert.Empty(CardapioConfirmacaoRules.VariantesTelefone(telefone));
        }

        [Fact]
        public void Whatsapp_link_carries_the_prefilled_message_with_the_code()
        {
            var link = CardapioConfirmacaoRules.LinkWhatsapp("+55 (34) 3333-0000", "4821");

            Assert.NotNull(link);
            Assert.StartsWith("https://wa.me/553433330000?text=", link);
            Assert.EndsWith(Uri.EscapeDataString(CardapioConfirmacaoRules.MensagemDoLink("4821")), link);
            // O que o cliente envia tem que ser reconhecido de volta pelo webhook.
            var extraido = CardapioConfirmacaoRules.ExtrairCodigo(CardapioConfirmacaoRules.MensagemDoLink("4821"));
            Assert.Equal("4821", extraido!.Value.Codigo);
        }

        [Theory]
        [InlineData("34999990000", "https://wa.me/5534999990000?text=")]
        [InlineData("5534999990000", "https://wa.me/5534999990000?text=")]
        public void Whatsapp_link_assumes_brazil_when_the_country_code_is_missing(string telefone, string inicio)
        {
            Assert.StartsWith(inicio, CardapioConfirmacaoRules.LinkWhatsapp(telefone, "1234"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("123")]
        public void Whatsapp_link_is_null_when_the_store_has_no_phone(string? telefone)
        {
            Assert.Null(CardapioConfirmacaoRules.LinkWhatsapp(telefone, "1234"));
        }
    }

    public class CardapioConfirmacaoTextosTests
    {
        [Theory]
        [InlineData(64.9, "R$ 64,90")]
        [InlineData(1234.5, "R$ 1.234,50")]
        [InlineData(0, "R$ 0,00")]
        public void Money_is_formatted_in_brazilian_style_regardless_of_server_culture(double valor, string esperado)
        {
            Assert.Equal(esperado, CardapioConfirmacaoRules.Dinheiro((decimal)valor));
        }

        [Fact]
        public void Item_line_lists_extras_only_when_there_are_any()
        {
            Assert.Equal("1x Coca 2L", CardapioConfirmacaoRules.LinhaDoItem(1, "Coca 2L", Array.Empty<string>()));
            Assert.Equal("2x X-Bacon (+ Bacon extra, Ovo)", CardapioConfirmacaoRules.LinhaDoItem(2, "X-Bacon", new[] { "Bacon extra", "Ovo" }));
        }

        [Fact]
        public void Declined_message_includes_the_reason_only_when_given()
        {
            Assert.Contains("Motivo: item em falta.", CardapioConfirmacaoRules.PedidoRecusado("Maria", "Pizza Bom", "item em falta"));
            Assert.DoesNotContain("Motivo", CardapioConfirmacaoRules.PedidoRecusado("Maria", "Pizza Bom", "  "));
        }

        [Fact]
        public void Reason_is_trimmed_empty_becomes_null_and_long_is_cut()
        {
            Assert.Null(CardapioConfirmacaoRules.NormalizarMotivo("   "));
            Assert.Null(CardapioConfirmacaoRules.NormalizarMotivo(null));
            Assert.Equal("fora da area", CardapioConfirmacaoRules.NormalizarMotivo("  fora da area "));
            Assert.Equal(CardapioConfirmacaoRules.MaxMotivoRecusa, CardapioConfirmacaoRules.NormalizarMotivo(new string('a', 500))!.Length);
        }

        [Fact]
        public void Name_fallback_never_leaves_a_hole_in_the_sentence()
        {
            var texto = CardapioConfirmacaoRules.PedidoRecusado("  ", "Pizza Bom", null);

            Assert.StartsWith("tudo bem,", texto);
            Assert.Contains("Pizza Bom", texto);
        }
    }
}
