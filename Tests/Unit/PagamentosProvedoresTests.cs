using System;
using System.IO;
using System.Linq;
using System.Reflection;
using APIBack.Payments.Conexao;
using APIBack.Payments.Controllers;
using APIBack.Payments.MercadoPago;
using APIBack.Payments.Provedores;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class PagamentosProvedoresTests
    {
        [Fact]
        public void MercadoPagoContinuaPadraoEComPixNativo()
        {
            var preferencias = new PreferenciasProvedorDto();
            var capacidades = ProvedorCapacidadesCatalogo.Obter(preferencias.CheckoutOnline, FluxosPagamento.CheckoutOnline);

            Assert.Equal(ProvedoresPagamento.MercadoPago, preferencias.CheckoutOnline);
            Assert.Equal(ProvedoresPagamento.MercadoPago, preferencias.PixExclusivo);
            Assert.True(capacidades.PixEmbedded);
            Assert.True(capacidades.PixExclusivo);
            Assert.True(capacidades.EstornoApi);
            Assert.False(capacidades.CheckoutHospedado);
        }

        [Fact]
        public void InfinitePayRepresentaCheckoutHospedadoSemInventarPixLocal()
        {
            var c = ProvedorCapacidadesCatalogo.Obter(ProvedoresPagamento.InfinitePay, FluxosPagamento.CheckoutOnline);

            Assert.True(c.CheckoutHospedado);
            Assert.True(c.CartaoHospedado);
            Assert.True(c.DescontoPix);
            Assert.False(c.PixEmbedded);
            Assert.False(c.PixExclusivo);
            Assert.False(c.EstornoApi);
        }

        [Fact]
        public void HandleENormalizadoEMasNuncaAceitaCifraoOuEspaco()
        {
            Assert.Equal("minhaloja", InfinitePayConfiguracaoRegras.NormalizarHandle("  MinhaLoja  "));
            Assert.Throws<PagamentoException>(() => InfinitePayConfiguracaoRegras.NormalizarHandle("$minhaloja"));
            Assert.Throws<PagamentoException>(() => InfinitePayConfiguracaoRegras.NormalizarHandle("minha loja"));
        }

        [Fact]
        public void ApiKeyTemFormatoDeServidorENaoExisteNaResposta()
        {
            Assert.Equal("inchk_segredo123", InfinitePayConfiguracaoRegras.ValidarApiKey("inchk_segredo123"));
            Assert.Throws<PagamentoException>(() => InfinitePayConfiguracaoRegras.ValidarApiKey("publica"));
            Assert.Null(typeof(InfinitePayConfiguracaoDto).GetProperty("ApiKey"));
            Assert.Null(typeof(InfinitePayConfiguracaoDto).GetProperty("ApiKeyCifrada"));
        }

        [Fact]
        public void ApiKeyCifradaFicaAmarradaAVersaoCorreta()
        {
            var chave = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();
            var protector = new PagamentoSegredoProtector(chave);
            var versaoA = Guid.NewGuid();
            var versaoB = Guid.NewGuid();
            var cifrada = protector.Proteger("inchk_segredo123", SegredoContexto.InfinitePayApiKey(versaoA));

            Assert.Equal("inchk_segredo123", protector.Revelar(cifrada, SegredoContexto.InfinitePayApiKey(versaoA)));
            Assert.Null(protector.Revelar(cifrada, SegredoContexto.InfinitePayApiKey(versaoB)));
            Assert.DoesNotContain("inchk_segredo123", cifrada, StringComparison.Ordinal);
        }

        [Fact]
        public void SnapshotRecusaContaDeOutraLojaENaoMudaComNovaPreferencia()
        {
            var loja = Guid.NewGuid();
            Assert.Throws<PagamentoException>(() => VinculoProvedorPagamento.InfinitePay(loja, Guid.NewGuid(), Guid.NewGuid()));

            var versao = Guid.NewGuid();
            var snapshot = VinculoProvedorPagamento.InfinitePay(loja, loja, versao);
            var novaPreferencia = new PreferenciasProvedorRequest { CheckoutOnline = ProvedoresPagamento.MercadoPago };

            Assert.Equal(ProvedoresPagamento.InfinitePay, snapshot.Provedor);
            Assert.Equal(versao, snapshot.IdContaVersao);
            Assert.Equal(ProvedoresPagamento.MercadoPago, novaPreferencia.CheckoutOnline);
        }

        [Fact]
        public void MigrationEAdditivaComSnapshotVerifyERollbackProtegido()
        {
            var sql = Migration("20261104_10_pagamentos_multigateway.sql");
            var verify = Migration("20261104_11_pagamentos_multigateway_verify.sql");
            var rollback = Migration(Path.Combine("rollback", "20261104_10_pagamentos_multigateway.down.sql"));

            Assert.Contains("DEFAULT 'mercadopago'", sql, StringComparison.Ordinal);
            Assert.Contains("id_infinitepay_conta_versao", sql, StringComparison.Ordinal);
            Assert.Contains("infinitepay_conta_versao", sql, StringComparison.Ordinal);
            Assert.Contains("FOREIGN KEY (id_estabelecimento, id_infinitepay_conta_versao)", sql, StringComparison.Ordinal);
            Assert.Contains("origem = 'cardapio'", sql, StringComparison.Ordinal);
            Assert.Contains("Mais de uma versao InfinitePay ativa", verify, StringComparison.Ordinal);
            Assert.Contains("Rollback bloqueado", rollback, StringComparison.Ordinal);
        }

        [Fact]
        public void RotasDeConfiguracaoNaoSaoAnonimas()
        {
            Assert.Null(typeof(InfinitePayIntegracaoController).GetCustomAttribute<AllowAnonymousAttribute>());
            Assert.Null(typeof(ProvedoresPagamentoController).GetCustomAttribute<AllowAnonymousAttribute>());
            Assert.All(typeof(InfinitePayIntegracaoController).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
                m => Assert.Null(m.GetCustomAttribute<AllowAnonymousAttribute>()));
        }

        private static string Migration(string nome)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", nome);
            Assert.True(File.Exists(path), $"Migration ausente: {path}");
            return File.ReadAllText(path);
        }
    }
}
