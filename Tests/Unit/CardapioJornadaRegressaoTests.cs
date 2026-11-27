using System;
using System.Net;
using APIBack.Controllers;
using APIBack.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace APIBack.Tests.Unit
{
    /// <summary>Regressoes da jornada de compra do cardapio web: telefone com e sem o nono digito e o teto atras do proxy.</summary>
    public class CardapioJornadaRegressaoTests
    {
        [Theory]
        // O cliente digitou com o 9 e o WhatsApp entrega sem: tem de ser reconhecido como o mesmo numero.
        [InlineData("+553491230001", "+5534991230001")]
        // O contrario: pre-pedido antigo sem o 9.
        [InlineData("+5534991230001", "+553491230001")]
        public void O_codigo_vale_para_o_mesmo_numero_com_e_sem_o_nono_digito(string daConversa, string doPedido) =>
            Assert.Contains(doPedido, CardapioConfirmacaoRules.TelefonesEquivalentes(daConversa));

        [Fact]
        public void Outro_numero_continua_sendo_outro_numero()
        {
            var equivalentes = CardapioConfirmacaoRules.TelefonesEquivalentes("+5534991230001");

            Assert.DoesNotContain("+5534991230002", equivalentes);
            Assert.DoesNotContain("+5511991230001", equivalentes);
        }

        [Fact]
        public void Numero_fora_do_padrao_brasileiro_vale_como_veio() =>
            Assert.Equal(new[] { "+1202555" }, CardapioConfirmacaoRules.TelefonesEquivalentes("+1202555"));

        private static HttpContext Requisicao(string conexao, string? encaminhado = null)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse(conexao);
            if (encaminhado != null) context.Request.Headers["X-Forwarded-For"] = encaminhado;
            return context;
        }

        private static bool Pedir(IMemoryCache cache, HttpContext context) =>
            LimitePublicoDoCardapio.Permitir(context, cache, "teste", 2, TimeSpan.FromMinutes(10));

        [Fact]
        public void Atras_do_proxy_um_cliente_que_estoura_o_teto_nao_bloqueia_os_outros()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var maria = Requisicao("10.0.0.5", "189.10.0.1, 10.0.0.9");
            var joao = Requisicao("10.0.0.5", "189.10.0.2");

            Assert.True(Pedir(cache, maria));
            Assert.True(Pedir(cache, maria));
            Assert.False(Pedir(cache, maria));
            Assert.True(Pedir(cache, joao));
        }

        [Fact]
        public void Cabecalho_forjado_ainda_encontra_o_teto_geral_do_proxy()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var permitidas = 0;
            for (var i = 0; i < 100; i++)
            {
                if (Pedir(cache, Requisicao("10.0.0.5", $"189.10.1.{i}"))) permitidas++;
            }

            Assert.Equal(2 * LimitePublicoDoCardapio.FolgaDoProxy, permitidas);
        }

        [Fact]
        public void Conexao_direta_ignora_o_cabecalho()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());

            Assert.True(Pedir(cache, Requisicao("200.1.1.1", "1.1.1.1")));
            Assert.True(Pedir(cache, Requisicao("200.1.1.1", "2.2.2.2")));
            Assert.False(Pedir(cache, Requisicao("200.1.1.1", "3.3.3.3")));
        }
    }
}
