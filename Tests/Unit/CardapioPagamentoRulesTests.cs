using System;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class CardapioPagamentoRulesTests
    {
        private static readonly Guid Pre = Guid.Parse("66666666-6666-6666-6666-666666666666");
        private static readonly DateTimeOffset Agora = new(2026, 10, 25, 20, 0, 0, TimeSpan.Zero);

        [Theory]
        [InlineData("pix_online", "pix")]
        [InlineData("Pix online", "pix")]
        [InlineData("cartao_online", "cartao")]
        [InlineData("Cartão online", "cartao")]
        [InlineData("Pix", null)]
        [InlineData("Cartão", null)]
        [InlineData("Dinheiro", null)]
        [InlineData(null, null)]
        public void Online_e_so_o_que_o_checkout_marca_como_online(string? forma, string? esperado) =>
            Assert.Equal(esperado, CardapioPagamentoRules.MetodoOnline(forma));

        [Fact]
        public void Confirmar_o_numero_nao_leva_pedido_online_sem_dinheiro_ao_restaurante()
        {
            Assert.Equal(CardapioPedidoStatus.AguardandoPagamento, CardapioPagamentoRules.StatusAposConfirmar(pagamentoOnline: true, pago: false));
            Assert.Equal(CardapioPedidoStatus.AguardandoAceite, CardapioPagamentoRules.StatusAposConfirmar(pagamentoOnline: true, pago: true));
            Assert.Equal(CardapioPedidoStatus.AguardandoAceite, CardapioPagamentoRules.StatusAposConfirmar(pagamentoOnline: false, pago: false));
        }

        [Fact]
        public void Chaves_de_idempotencia_seguem_o_contrato()
        {
            Assert.Equal($"cardapio:{Pre:N}", CardapioPagamentoRules.ChavePix(Pre, 1));
            Assert.Equal($"cardapio:{Pre:N}:pix:2", CardapioPagamentoRules.ChavePix(Pre, 2));
            Assert.Equal($"cardapio:{Pre:N}:cartao", CardapioPagamentoRules.ChaveCartao(Pre, 1));
            Assert.Equal($"cardapio:{Pre:N}:cartao:3", CardapioPagamentoRules.ChaveCartao(Pre, 3));
            Assert.True(CardapioPagamentoRules.EhChaveCartao(CardapioPagamentoRules.ChaveCartao(Pre, 1)));
            Assert.False(CardapioPagamentoRules.EhChaveCartao(CardapioPagamentoRules.ChavePix(Pre, 2)));
            // A mesma decisao gera a mesma chave (recusa clicada duas vezes = um reembolso).
            var intent = Guid.NewGuid();
            Assert.Equal(CardapioPagamentoRules.ChaveReembolso(Pre, intent, "recusa_loja"), CardapioPagamentoRules.ChaveReembolso(Pre, intent, "recusa_loja"));
        }

        [Theory]
        [InlineData(CardapioPedidoStatus.AguardandoPagamento, 0, null, FaseCancelamento.AntesDoAceite)]
        [InlineData(CardapioPedidoStatus.AguardandoAceite, 0, null, FaseCancelamento.AntesDoAceite)]
        [InlineData(CardapioPedidoStatus.Aceitando, 0, null, FaseCancelamento.AceitoAntesDoPreparo)]
        [InlineData(CardapioPedidoStatus.Aceito, 2, "pendente", FaseCancelamento.AceitoAntesDoPreparo)]
        [InlineData(CardapioPedidoStatus.Aceito, 2, null, FaseCancelamento.AceitoAntesDoPreparo)]
        [InlineData(CardapioPedidoStatus.Aceito, 20, "pendente", FaseCancelamento.EmPreparoOuEntrega)]
        [InlineData(CardapioPedidoStatus.Aceito, 2, "em_rota", FaseCancelamento.EmPreparoOuEntrega)]
        [InlineData(CardapioPedidoStatus.Aceito, 2, "atribuido", FaseCancelamento.EmPreparoOuEntrega)]
        [InlineData(CardapioPedidoStatus.Recusado, 0, null, FaseCancelamento.Encerrado)]
        [InlineData(CardapioPedidoStatus.Cancelado, 0, null, FaseCancelamento.Encerrado)]
        public void Fase_do_cancelamento(string status, int minutosDesdeOAceite, string? statusReal, FaseCancelamento esperada) =>
            Assert.Equal(esperada, CardapioPagamentoRules.Fase(status, Agora.AddMinutes(-minutosDesdeOAceite), statusReal, Agora));

        [Theory]
        [InlineData(false, null, null, "nenhum")]
        [InlineData(true, null, null, "aguardando")]
        [InlineData(true, "aguardando_pagamento", null, "aguardando")]
        [InlineData(true, "processando", null, "processando")]
        [InlineData(true, "pago", null, "aprovado")]
        [InlineData(true, "recusado", null, "recusado")]
        [InlineData(true, "expirado", null, "expirado")]
        // Reembolso decidido mas ainda nao confirmado pelo Mercado Pago nunca aparece como reembolsado.
        [InlineData(true, "pago", "reembolso_pendente", "reembolso_pendente")]
        [InlineData(true, "pago", "aguardando_aprovacao", "reembolso_pendente")]
        [InlineData(true, "pago", "falhou", "reembolso_pendente")]
        [InlineData(true, "pago", "reembolso_em_processamento", "reembolso_em_processamento")]
        [InlineData(true, "estornado", "reembolsado", "reembolsado")]
        public void Status_que_o_cliente_ve(bool online, string? intent, string? reembolso, string esperado) =>
            Assert.Equal(esperado, CardapioPagamentoRules.StatusParaCliente(online, intent, reembolso, 50m, intent == "estornado" ? 50m : 0m));

        [Fact]
        public void Estorno_parcial_aparece_como_parcial()
        {
            Assert.Equal("reembolsado_parcial", CardapioPagamentoRules.StatusParaCliente(true, "estornado_parcial", "reembolsado", 50m, 20m));
        }

        [Fact]
        public void O_pedido_real_avisa_que_nao_ha_nada_a_cobrar_na_entrega()
        {
            Assert.Equal("Pago online - Pix", CardapioPagamentoRules.FormaPagamentoDoPedido("pix"));
            Assert.Equal("Pago online - cartao", CardapioPagamentoRules.FormaPagamentoDoPedido("cartao"));
            Assert.Contains("devolvido integralmente", CardapioPagamentoRules.PedidoRecusadoComEstorno("Maria Souza", "Pizza Bom", null, 42m));
        }
    }
}
