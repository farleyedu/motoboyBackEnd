using System;
using System.Linq;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class PedidoHistoricoBuilderTests
    {
        private static readonly DateTimeOffset T0 = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public void Build_FullDelivery_ListsEveryMilestoneInOrder()
        {
            var stop = new PedidoHistoricoStopRow
            {
                Id = 1, MotoboyId = 7, MotoboyNome = "Pedro", StopStatus = "completed",
                AssignedAtUtc = T0.AddMinutes(1),
                StartedAtUtc = T0.AddMinutes(1),
                PickedUpAtUtc = T0.AddMinutes(6),
                ArrivedAtUtc = T0.AddMinutes(18),
                CompletedAtUtc = T0.AddMinutes(20),
                CompletedBy = "motoboy",
            };

            var result = PedidoHistoricoBuilder.Build(10, T0, new[] { stop }, Array.Empty<PedidoHistoricoTransferRow>());

            Assert.Equal(
                new[] { "criado", "atribuido", "entrega_iniciada", "coletado", "chegou", "entregue" },
                result.Eventos.Select(item => item.Tipo).ToArray());
            Assert.All(result.Eventos.Skip(1), item => Assert.Equal("Pedro", item.MotoboyNome));
            Assert.Equal("motoboy", result.Eventos.Last().Ator);
        }

        [Fact]
        public void Build_FailedThenDeliveredByAnother_KeepsBothAttemptsAndTheReason()
        {
            var first = new PedidoHistoricoStopRow
            {
                Id = 1, MotoboyId = 7, MotoboyNome = "Pedro", StopStatus = "failed",
                AssignedAtUtc = T0.AddMinutes(1),
                FailedAtUtc = T0.AddMinutes(25),
                FailureReason = "  Cliente ausente ",
            };
            var second = new PedidoHistoricoStopRow
            {
                Id = 2, MotoboyId = 9, MotoboyNome = "Joana", StopStatus = "completed",
                AssignedAtUtc = T0.AddMinutes(30),
                CompletedAtUtc = T0.AddMinutes(50),
                CompletedBy = "operator",
            };

            var result = PedidoHistoricoBuilder.Build(10, null, new[] { second, first }, Array.Empty<PedidoHistoricoTransferRow>());

            var failed = Assert.Single(result.Eventos, item => item.Tipo == "nao_entregue");
            Assert.Equal("Cliente ausente", failed.Motivo);
            Assert.Equal("Pedro", failed.MotoboyNome);

            var delivered = Assert.Single(result.Eventos, item => item.Tipo == "entregue");
            Assert.Equal("Joana", delivered.MotoboyNome);
            Assert.Equal("atendente", delivered.Ator);

            Assert.True(result.Eventos.Zip(result.Eventos.Skip(1)).All(pair => pair.First.OcorridoEmUtc <= pair.Second.OcorridoEmUtc));
        }

        [Fact]
        public void Build_TransferredStop_ShowsTransferEventsAndNotARemoval()
        {
            var stop = new PedidoHistoricoStopRow
            {
                Id = 1, MotoboyId = 7, MotoboyNome = "Pedro", StopStatus = "removed",
                AssignedAtUtc = T0.AddMinutes(1),
                RemovedAtUtc = T0.AddMinutes(10),
                TransferredAtUtc = T0.AddMinutes(10),
                TransferRequestId = 3,
            };
            var transfer = new PedidoHistoricoTransferRow
            {
                Id = 3, FromMotoboyId = 7, FromMotoboyNome = "Pedro", ToMotoboyId = 9, ToMotoboyNome = "Joana",
                Status = "completed", RequestedBy = "operator", Reason = "Moto quebrou",
                RequestedAtUtc = T0.AddMinutes(10), CompletedAtUtc = T0.AddMinutes(10),
            };

            var result = PedidoHistoricoBuilder.Build(10, null, new[] { stop }, new[] { transfer });

            Assert.DoesNotContain(result.Eventos, item => item.Tipo == "removido_da_fila");
            var requested = Assert.Single(result.Eventos, item => item.Tipo == "transferencia_solicitada");
            Assert.Equal("De Pedro para Joana", requested.Detalhe);
            Assert.Equal("Moto quebrou", requested.Motivo);
            Assert.Equal("atendente", requested.Ator);
            var completed = Assert.Single(result.Eventos, item => item.Tipo == "transferencia_concluida");
            Assert.Equal("Joana", completed.MotoboyNome);
        }

        [Fact]
        public void Build_RejectedTransfer_RecordsTheDecisionNote()
        {
            var transfer = new PedidoHistoricoTransferRow
            {
                Id = 4, FromMotoboyId = 7, ToMotoboyId = 9, Status = "rejected", RequestedBy = "motoboy",
                RequestedAtUtc = T0, DecidedAtUtc = T0.AddMinutes(2), DecisionNote = "Sem motoboy livre",
            };

            var result = PedidoHistoricoBuilder.Build(10, null, Array.Empty<PedidoHistoricoStopRow>(), new[] { transfer });

            var rejected = Assert.Single(result.Eventos, item => item.Tipo == "transferencia_rejeitada");
            Assert.Equal("Sem motoboy livre", rejected.Motivo);
            Assert.Equal("De motoboy 7 para motoboy 9", rejected.Detalhe);
        }

        [Fact]
        public void Build_WithoutAnyRows_ReturnsEmptyTimeline()
        {
            var result = PedidoHistoricoBuilder.Build(10, null, Array.Empty<PedidoHistoricoStopRow>(), Array.Empty<PedidoHistoricoTransferRow>());

            Assert.Equal(10, result.PedidoId);
            Assert.Empty(result.Eventos);
        }
    }
}
