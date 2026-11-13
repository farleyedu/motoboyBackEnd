using System;
using System.Collections.Generic;
using System.IO;
using APIBack.Payments.Conexao;
using APIBack.Payments.InfinitePay.Checkout;
using APIBack.Payments.InfinitePay.Cliente;
using APIBack.Payments.MercadoPago;
using Xunit;

namespace APIBack.Tests.Unit
{
    /// <summary>InfinitePay da Zippy (mensalidade e compra extra): regras puras, isolamento de segredo e forma da migration.</summary>
    public sealed class InfinitePayZippyTests
    {
        private static InfinitePayCheckoutRequest Cobranca(string origem, Guid? refId) => new()
        {
            IdEstabelecimento = Guid.NewGuid(), Origem = origem, OrigemRefId = refId, Valor = 99.90m, Descricao = "Plano Pro (mensal)",
            IdempotencyKey = "assinatura:abc:link",
            Itens = new List<InfinitePayItem> { new() { Description = "Plano Pro (mensal)", Price = 9990, Quantity = 1 } }
        };

        [Theory]
        [InlineData("assinatura", true)]
        [InlineData("compra_extra", true)]
        [InlineData("cardapio", false)]
        [InlineData("chat", false)]
        public void SoMensalidadeEExtraUsamAContaDaZippy(string origem, bool esperado) =>
            Assert.Equal(esperado, InfinitePayCheckoutMontagem.EhOrigemZippy(origem));

        [Theory]
        [InlineData("assinatura")]
        [InlineData("compra_extra")]
        public void CobrancaDaZippyExigeAReferenciaDaCobranca(string origem)
        {
            var ex = Assert.Throws<PagamentoException>(() => InfinitePayCheckoutMontagem.ValidarPedido(Cobranca(origem, null)));
            Assert.Contains("Cobranca obrigatoria", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void CobrancaDaZippyComReferenciaPassaNaValidacao() =>
            InfinitePayCheckoutMontagem.ValidarPedido(Cobranca("assinatura", Guid.NewGuid()));

        [Fact]
        public void OrigemDesconhecidaContinuaRecusada() =>
            Assert.Throws<PagamentoException>(() => InfinitePayCheckoutMontagem.ValidarPedido(Cobranca("outra", Guid.NewGuid())));

        [Fact]
        public void ChaveDaZippyNaoAbreNoContextoDeLojaENemTrocaDeVersao()
        {
            var versao = Guid.NewGuid();
            Assert.NotEqual(SegredoContexto.InfinitePayApiKey(versao), SegredoContexto.InfinitePayZippyApiKey(versao));
            Assert.NotEqual(SegredoContexto.InfinitePayZippyApiKey(versao), SegredoContexto.InfinitePayZippyApiKey(Guid.NewGuid()));
        }

        [Fact]
        public void ConfiguracaoDaZippyUsaAsMesmasRegrasDeTagEChaveDaLoja()
        {
            Assert.Equal("zippy", InfinitePayConfiguracaoRegras.NormalizarHandle(" Zippy "));
            Assert.Throws<PagamentoException>(() => InfinitePayConfiguracaoRegras.NormalizarHandle("$zippy"));
            Assert.Throws<PagamentoException>(() => InfinitePayConfiguracaoRegras.ValidarApiKey("chave-sem-prefixo"));
        }

        [Fact]
        public void LinkEUmMetodoPropriosemVirarPixOuCartao()
        {
            Assert.Equal("link", MetodosPagamento.Link);
            Assert.NotEqual(MetodosPagamento.Pix, MetodosPagamento.Link);
            Assert.NotEqual(MetodosPagamento.Cartao, MetodosPagamento.Link);
        }

        [Fact]
        public void MigrationDaContaZippyIsolaContasEAbreSoOsNovosValoresDeCobranca()
        {
            var sql = File.ReadAllText(Caminho("Migrations", "Delivery", "20261202_01_infinitepay_conta_zippy.sql"));
            Assert.Contains("ck_infinitepay_checkout_conta", sql, StringComparison.Ordinal);
            Assert.Contains("(id_conta_versao IS NOT NULL)::int + (id_conta_zippy_versao IS NOT NULL)::int = 1", sql, StringComparison.Ordinal);
            Assert.Contains("provedor = 'infinitepay' AND origem IN ('assinatura', 'compra_extra') AND conta_tipo = 'zippy'", sql, StringComparison.Ordinal);
            Assert.Contains("id_infinitepay_zippy_versao IS NOT NULL", sql, StringComparison.Ordinal);
            Assert.Contains("'cartao_recorrente', 'manual', 'link'", sql, StringComparison.Ordinal);
            Assert.Contains("'mercadopago', 'mp_recorrente', 'manual', 'infinitepay'", sql, StringComparison.Ordinal);
            // Mercado Pago continua exatamente como era: o ramo MP exige que a coluna nova seja nula.
            Assert.Contains("id_infinitepay_conta_versao IS NULL AND id_infinitepay_zippy_versao IS NULL", sql, StringComparison.Ordinal);
        }

        [Fact]
        public void RollbackDaContaZippyRecusaQuandoJaHaCobranca()
        {
            var sql = File.ReadAllText(Caminho("Migrations", "Delivery", "rollback", "20261202_01_infinitepay_conta_zippy.down.sql"));
            Assert.Contains("RAISE EXCEPTION", sql, StringComparison.Ordinal);
            Assert.Contains("DROP TABLE IF EXISTS infinitepay_conta_zippy_versao", sql, StringComparison.Ordinal);
        }

        private static string Caminho(params string[] partes)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "APIBack.csproj"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir!.FullName }.Concat2(partes));
        }
    }

    internal static class CaminhoExt
    {
        public static string[] Concat2(this string[] a, string[] b)
        {
            var r = new string[a.Length + b.Length];
            a.CopyTo(r, 0);
            b.CopyTo(r, a.Length);
            return r;
        }
    }
}
