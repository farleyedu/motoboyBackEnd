using APIBack.Integrations.Ifood.Contracts;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class IfoodOrderContractTests
{
    private static IfoodOrderIdentity Identity(string? display = "001234") => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 23, display);
    private static IfoodOrderSnapshot Snapshot(string type = "DELIVERY", string? deliveredBy = "MERCHANT",
        decimal? pending = 45m, string? currency = "BRL") => new(Identity(), 1, type, deliveredBy, "CONFIRMED", currency, 65m, 20m, pending);

    [Fact]
    public void DisplayNumberPreservesZerosAndNeverBecomesExternalIdentity()
    {
        var identity = Identity(" 001234 ");
        Assert.Equal("001234", identity.DisplayNumber);
        Assert.Equal("iFood #001234", identity.DisplayLabel);
        Assert.NotEqual(identity.ExternalOrderId.ToString(), identity.DisplayNumber);
        Assert.Equal("iFood · número indisponível", Identity(" ").DisplayLabel);
    }

    [Fact]
    public void OfficialIdentityRejectsMissingKeys()
    {
        var id = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => new IfoodOrderIdentity(Guid.Empty, id, id, 23, "1"));
        Assert.Throws<ArgumentException>(() => new IfoodOrderIdentity(id, Guid.Empty, id, 23, "1"));
        Assert.Throws<ArgumentException>(() => new IfoodOrderIdentity(id, id, Guid.Empty, 23, "1"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IfoodOrderIdentity(id, id, id, 0, "1"));
    }

    [Theory]
    [InlineData("DELIVERY", "MERCHANT", true)]
    [InlineData("delivery", "merchant", true)]
    [InlineData("DELIVERY", "IFOOD", false)]
    [InlineData("TAKEOUT", "MERCHANT", false)]
    [InlineData("DINE_IN", null, false)]
    [InlineData("FUTURE_TYPE", "MERCHANT", false)]
    [InlineData("DELIVERY", "FUTURE_CARRIER", false)]
    public void OnlyMerchantDeliveryHasZippyClassification(string type, string? carrier, bool expected)
        => Assert.Equal(expected, Snapshot(type, carrier).IsMerchantDelivery);

    [Fact]
    public void CollectsPendingAmountRatherThanTotalAndKeepsZeroDistinctFromMissing()
    {
        Assert.Equal(45m, Snapshot().ExternalAmountToCollect);
        Assert.Equal(0m, Snapshot(pending: 0m).ExternalAmountToCollect);
        Assert.Null(Snapshot(pending: null).ExternalAmountToCollect);
        Assert.True(Snapshot(pending: null).RequiresPaymentReview);
        Assert.Null(Snapshot(currency: "UNKNOWN").ExternalAmountToCollect);
        Assert.True(Snapshot(currency: null).RequiresPaymentReview);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1.001)]
    public void MoneyCannotBeNegativeOrSilentlyRounded(decimal amount)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(pending: amount));

    [Fact]
    public void OversizedMoneyAndInvertedOrIncompleteScheduleAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(pending: decimal.MaxValue));
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => new IfoodOrderSnapshot(Identity(), 1, "TAKEOUT", null, null, "BRL", 0, 0, 0, now, null));
        Assert.Throws<ArgumentException>(() => new IfoodOrderSnapshot(Identity(), 1, "TAKEOUT", null, null, "BRL", 0, 0, 0, now, now.AddMinutes(-1)));
    }

    [Fact]
    public void UnknownRemoteValuesArePreservedWithoutAssumingDeliveryOrSuccess()
    {
        var snapshot = new IfoodOrderSnapshot(Identity(), 1, "FUTURE_TYPE", "FUTURE_CARRIER", "FUTURE_STATUS", null, null, null, null);
        Assert.Equal("FUTURE_STATUS", snapshot.RemoteStatus);
        Assert.False(snapshot.IsMerchantDelivery);
        Assert.True(snapshot.RequiresPaymentReview);
        Assert.Equal(IfoodOperationState.Unknown, IfoodOperationStateCodes.FromCode("future_success"));
        Assert.Throws<ArgumentOutOfRangeException>(() => IfoodOperationState.Unknown.ToCode());
        Assert.Equal(IfoodOperationState.Uncertain, IfoodOperationStateCodes.FromCode("uncertain"));
        Assert.NotEqual(IfoodOperationState.Succeeded, IfoodOperationStateCodes.FromCode("uncertain"));
    }
}
