using System.Linq;
using APIBack.Model.Enum;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class SimuladorPedidoRulesTests
    {
        [Theory]
        [InlineData("confirmar", SimEtapa.Confirmar)]
        [InlineData("Em preparo", SimEtapa.Preparo)]
        [InlineData("saiu_para_entrega", SimEtapa.Saiu)]
        [InlineData("entregue", SimEtapa.Entregue)]
        public void ParseEtapa_AcceptsTheNamesTheScreenUses(string raw, SimEtapa expected) =>
            Assert.Equal(expected, SimuladorPedidoRules.ParseEtapa(raw));

        [Fact]
        public void ParseEtapa_RejectsUnknown() => Assert.Null(SimuladorPedidoRules.ParseEtapa("voar"));

        [Theory]
        [InlineData("recebido", SimAlvo.Recebido)]
        [InlineData("em_rota", SimAlvo.Saiu)]
        [InlineData("concluido", SimAlvo.Entregue)]
        [InlineData("cancelado", SimAlvo.Cancelado)]
        public void ParseAlvo_MapsScreenAndSystemNames(string raw, SimAlvo expected) =>
            Assert.Equal(expected, SimuladorPedidoRules.ParseAlvo(raw));

        [Fact]
        public void Etapas_FollowTheOrder_ConfirmThenPrepThenLeaveThenDeliver()
        {
            // recebido: so confirmar
            Assert.Equal(new[] { "confirmar" }, SimuladorPedidoRules.Proximos(StatusPedido.Pendente, false, false));
            // confirmado: so preparo
            Assert.Equal(new[] { "preparo" }, SimuladorPedidoRules.Proximos(StatusPedido.Pendente, true, false));
            // em preparo: pode sair
            Assert.Equal(new[] { "saiu" }, SimuladorPedidoRules.Proximos(StatusPedido.Pendente, true, true));
            // em rota: so entregar
            Assert.Equal(new[] { "entregue" }, SimuladorPedidoRules.Proximos(StatusPedido.EmRota, true, true));
        }

        [Fact]
        public void CannotLeaveWithoutConfirmAndPrep_NorDeliverWithoutBeingInRoute()
        {
            Assert.NotNull(SimuladorPedidoRules.ValidateEtapa(SimEtapa.Saiu, StatusPedido.Pendente, false, false));
            Assert.NotNull(SimuladorPedidoRules.ValidateEtapa(SimEtapa.Saiu, StatusPedido.Pendente, true, false));
            // Entregue de um pedido que so esta na fila (sem entrega atual) nao vale.
            Assert.NotNull(SimuladorPedidoRules.ValidateEtapa(SimEtapa.Entregue, StatusPedido.Atribuido, true, true));
            Assert.NotNull(SimuladorPedidoRules.ValidateEtapa(SimEtapa.Entregue, StatusPedido.Pendente, true, true));
        }

        [Theory]
        [InlineData(StatusPedido.Concluido)]
        [InlineData(StatusPedido.Cancelado)]
        [InlineData(StatusPedido.Rascunho)]
        public void FinishedOrDraft_AdvancesNothing(StatusPedido status)
        {
            foreach (var etapa in new[] { SimEtapa.Confirmar, SimEtapa.Preparo, SimEtapa.Saiu, SimEtapa.Entregue })
            {
                Assert.NotNull(SimuladorPedidoRules.ValidateEtapa(etapa, status, true, true));
            }
            Assert.Empty(SimuladorPedidoRules.Proximos(status, true, true));
        }

        [Fact]
        public void LeavingOrDeliveringAlwaysNeedsAMotoboy()
        {
            Assert.True(SimuladorPedidoRules.RequiresMotoboy(SimAlvo.Saiu));
            Assert.True(SimuladorPedidoRules.RequiresMotoboy(SimAlvo.Entregue));
            Assert.False(SimuladorPedidoRules.RequiresMotoboy(SimAlvo.Recebido));
            Assert.False(SimuladorPedidoRules.RequiresMotoboy(SimAlvo.Confirmado));
            Assert.False(SimuladorPedidoRules.RequiresMotoboy(SimAlvo.EmPreparo));
            Assert.False(SimuladorPedidoRules.RequiresMotoboy(SimAlvo.Cancelado));
        }

        [Fact]
        public void PathTo_IsCumulativeAndEveryStepIsValidInSequence()
        {
            Assert.Empty(SimuladorPedidoRules.PathTo(SimAlvo.Recebido));
            Assert.Equal(4, SimuladorPedidoRules.PathTo(SimAlvo.Entregue).Count);

            // Percorrer o caminho ate "entregue" nunca viola uma regra (simula o estado apos cada passo).
            var status = StatusPedido.Pendente; var confirmado = false; var preparo = false;
            foreach (var etapa in SimuladorPedidoRules.PathTo(SimAlvo.Entregue))
            {
                Assert.Null(SimuladorPedidoRules.ValidateEtapa(etapa, status, confirmado, preparo));
                switch (etapa)
                {
                    case SimEtapa.Confirmar: confirmado = true; break;
                    case SimEtapa.Preparo: preparo = true; break;
                    case SimEtapa.Saiu: status = StatusPedido.EmRota; break;
                    case SimEtapa.Entregue: status = StatusPedido.Concluido; break;
                }
            }
            Assert.Equal(StatusPedido.Concluido, status);
        }

        [Theory]
        [InlineData(StatusPedido.Pendente, false, false, "recebido")]
        [InlineData(StatusPedido.Pendente, true, false, "confirmado")]
        [InlineData(StatusPedido.Pendente, true, true, "em_preparo")]
        [InlineData(StatusPedido.Atribuido, true, true, "em_preparo")]
        [InlineData(StatusPedido.EmRota, true, true, "saiu")]
        [InlineData(StatusPedido.Concluido, true, true, "entregue")]
        [InlineData(StatusPedido.Cancelado, true, false, "cancelado")]
        public void EtapaAtual(StatusPedido status, bool confirmado, bool preparo, string expected) =>
            Assert.Equal(expected, SimuladorPedidoRules.EtapaAtual(status, confirmado, preparo));

        [Fact]
        public void DisplayId_MarksTestOrders()
        {
            Assert.Equal("TEST-7832", SimuladorPedidoRules.DisplayId(7832, true));
            Assert.Equal("7832", SimuladorPedidoRules.DisplayId(7832, false));
        }

        [Theory]
        [InlineData(null, "whatsapp")]
        [InlineData("App", "app")]
        [InlineData("balcao", "balcao")]
        [InlineData("telegram", null)]
        public void Channel(string? raw, string? expected) => Assert.Equal(expected, SimuladorPedidoRules.NormalizeChannel(raw));

        [Fact]
        public void ClienteEtiqueta_PriorityInactiveThenVipThenHumanThenFrequency()
        {
            Assert.Equal("Inativo", SimuladorPedidoRules.ClienteEtiqueta(false, new[] { "VIP" }, true, 10));
            Assert.Equal("VIP", SimuladorPedidoRules.ClienteEtiqueta(true, new[] { "cliente vip" }.Select(t => "Cliente VIP").ToArray(), true, 0));
            Assert.Equal("Em tratamento", SimuladorPedidoRules.ClienteEtiqueta(true, new string[0], true, 10));
            Assert.Equal("Frequente", SimuladorPedidoRules.ClienteEtiqueta(true, new string[0], false, 5));
            Assert.Equal("Novo", SimuladorPedidoRules.ClienteEtiqueta(true, new string[0], false, 4));
        }

        [Theory]
        [InlineData("Dinheiro", "dinheiro")]
        [InlineData("dinheiro", "dinheiro")]
        [InlineData("PIX", "pix")]
        [InlineData("pix", "pix")]
        [InlineData("Cartao", "cartao_entrega")]
        [InlineData("Cartão de crédito", "cartao_entrega")]
        [InlineData("Cartão de débito", "cartao_entrega")]
        [InlineData("cartao credito", "cartao_entrega")]
        [InlineData("Pago online", "link")]
        [InlineData("pago ONLINE", "link")]
        public void TryNormalizePayment_MapsEveryLabelToTheRealVocabulary(string raw, string expected)
        {
            Assert.True(SimuladorPedidoRules.TryNormalizePayment(raw, out var normalized));
            Assert.Equal(expected, normalized);
            Assert.Contains(normalized, SimuladorPedidoRules.PaymentTypes);
        }

        [Fact]
        public void TryNormalizePayment_CoincidesWithTheCoreVocabulary()
        {
            foreach (var raw in new[] { "Dinheiro", "PIX", "Cartao" })
            {
                Assert.True(SimuladorPedidoRules.TryNormalizePayment(raw, out var mine));
                Assert.Equal(OrderCoreRules.NormalizePayment(raw), mine);
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void TryNormalizePayment_BlankMeansNoPaymentInformed(string? raw)
        {
            Assert.True(SimuladorPedidoRules.TryNormalizePayment(raw, out var normalized));
            Assert.Null(normalized);
        }

        [Theory]
        [InlineData("cheque")]
        [InlineData("fiado")]
        [InlineData("boleto")]
        public void TryNormalizePayment_RejectsWhatTheSystemDoesNotKnow(string raw)
        {
            Assert.False(SimuladorPedidoRules.TryNormalizePayment(raw, out _));
        }
    }
}
