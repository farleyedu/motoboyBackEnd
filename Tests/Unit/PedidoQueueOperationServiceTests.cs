using System;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Repository.Interface;
using APIBack.Service;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    /// <summary>Validacoes das acoes do motoboy, transferencia, parametros e pedido manual.</summary>
    public sealed class PedidoQueueOperationServiceTests
    {
        private readonly Mock<IPedidoQueueRepository> _repository = new();
        private readonly Guid _estabelecimentoId = Guid.NewGuid();

        private PedidoQueueService CreateService() => new(_repository.Object);

        [Fact]
        public async Task FailCurrent_RequiresReason()
        {
            var exception = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                CreateService().FailCurrentAsync(_estabelecimentoId, 10, "   "));

            Assert.Equal("REASON_REQUIRED", exception.Code);
            _repository.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task FailCurrent_PassesTrimmedReason()
        {
            _repository.Setup(r => r.FailCurrentAsync(_estabelecimentoId, 10, "Cliente ausente"))
                .ReturnsAsync(new MotoboyQueueDto());

            await CreateService().FailCurrentAsync(_estabelecimentoId, 10, "  Cliente ausente  ");

            _repository.Verify(r => r.FailCurrentAsync(_estabelecimentoId, 10, "Cliente ausente"), Times.Once);
        }

        [Fact]
        public async Task Deliver_NormalizesEmptyCodeToNull()
        {
            _repository.Setup(r => r.DeliverCurrentAsync(_estabelecimentoId, 10, null)).ReturnsAsync(new MotoboyQueueDto());

            await CreateService().DeliverCurrentAsync(_estabelecimentoId, 10, "   ");

            _repository.Verify(r => r.DeliverCurrentAsync(_estabelecimentoId, 10, null), Times.Once);
        }

        [Fact]
        public async Task MotoboyTransfer_ToSelf_IsRejected()
        {
            var exception = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                CreateService().RequestTransferByMotoboyAsync(_estabelecimentoId, 10, 1, pedidoId: 5, toMotoboyId: 10, "moto quebrou"));

            Assert.Equal("TRANSFER_SAME_MOTOBOY", exception.Code);
            _repository.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task MotoboyTransfer_InvalidTarget_IsRejected()
        {
            await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                CreateService().RequestTransferByMotoboyAsync(_estabelecimentoId, 10, 1, pedidoId: 5, toMotoboyId: 0, null));
            _repository.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task OperatorTransfer_DelegatesWithNormalizedReason()
        {
            _repository.Setup(r => r.TransferByOperatorAsync(_estabelecimentoId, 1, 5, 11, null))
                .ReturnsAsync(new TransferResultDto());

            await CreateService().TransferByOperatorAsync(_estabelecimentoId, 1, 5, 11, "  ");

            _repository.Verify(r => r.TransferByOperatorAsync(_estabelecimentoId, 1, 5, 11, null), Times.Once);
        }

        [Fact]
        public async Task ListTransfers_DefaultsToPendingApprovals()
        {
            _repository.Setup(r => r.ListTransfersAsync(_estabelecimentoId, TransferStatuses.PendingApproval, 50))
                .ReturnsAsync(Array.Empty<TransferRequestDto>());

            await CreateService().ListTransfersAsync(_estabelecimentoId, null, 0);

            _repository.Verify(r => r.ListTransfersAsync(_estabelecimentoId, TransferStatuses.PendingApproval, 50), Times.Once);
        }

        [Fact]
        public async Task ListTransfers_AllMeansNoStatusFilter()
        {
            _repository.Setup(r => r.ListTransfersAsync(_estabelecimentoId, null, 20))
                .ReturnsAsync(Array.Empty<TransferRequestDto>());

            await CreateService().ListTransfersAsync(_estabelecimentoId, "ALL", 20);

            _repository.Verify(r => r.ListTransfersAsync(_estabelecimentoId, null, 20), Times.Once);
        }

        [Fact]
        public async Task ListTransfers_UnknownStatus_IsRejected()
        {
            await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                CreateService().ListTransfersAsync(_estabelecimentoId, "aprovado", 10));
            _repository.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("operator")]
        [InlineData("qualquer")]
        public async Task UpdateSettings_RejectsNonConfigurablePolicy(string? policy)
        {
            var exception = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                CreateService().UpdateSettingsAsync(_estabelecimentoId, 1, new UpdateDeliverySettingsRequest { TransferPolicy = policy }));

            Assert.Equal("INVALID_TRANSFER_POLICY", exception.Code);
            _repository.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task UpdateSettings_NormalizesPolicy()
        {
            _repository.Setup(r => r.UpsertSettingsAsync(_estabelecimentoId, 1,
                    It.Is<UpdateDeliverySettingsRequest>(x => x.TransferPolicy == TransferPolicies.EstablishmentApproval)))
                .ReturnsAsync(new DeliverySettingsDto());

            await CreateService().UpdateSettingsAsync(_estabelecimentoId, 1,
                new UpdateDeliverySettingsRequest { TransferPolicy = " Establishment_Approval " });

            _repository.Verify(r => r.UpsertSettingsAsync(_estabelecimentoId, 1,
                It.Is<UpdateDeliverySettingsRequest>(x => x.TransferPolicy == TransferPolicies.EstablishmentApproval)), Times.Once);
        }

        [Fact]
        public async Task MotoboyReorder_RejectsInvalidIds()
        {
            await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                CreateService().ReorderByMotoboyAsync(_estabelecimentoId, 10, 3, new[] { 5, 0 }));
            _repository.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task CreatePedido_InvalidRequest_NeverReachesRepository()
        {
            await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                CreateService().CreatePedidoAsync(_estabelecimentoId, 1, new CreatePedidoRequest { NomeCliente = "Sem endereco" }));
            _repository.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task Refuse_OptionalReasonIsNormalized()
        {
            _repository.Setup(r => r.RefuseAsync(_estabelecimentoId, 10, 5, null)).ReturnsAsync(new MotoboyQueueDto());

            await CreateService().RefuseAsync(_estabelecimentoId, 10, 5, "");

            _repository.Verify(r => r.RefuseAsync(_estabelecimentoId, 10, 5, null), Times.Once);
        }
    }
}
