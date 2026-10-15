using System.Text.Json.Nodes;
using APIBack.DTOs.Delivery;
using APIBack.DTOs.Tracking;
using APIBack.Integrations.Ifood.Commands;
using APIBack.Integrations.Ifood.Orders;
using APIBack.Repository;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit;

public class IfoodCommandPanelTests
{
    [Theory]
    [InlineData("CONFIRMED", true)]
    [InlineData("PLACED", true)]
    [InlineData("CONCLUDED", false)]
    [InlineData("CANCELLED", false)]
    public void WindowRecognizesOfficialOpenStateWithoutPromotingDraft(string remote, bool visible)
    {
        var order = new OrderMapDto { StatusPedido = "rascunho", IdentidadeExterna = new IfoodPedidoIdentityDto {
            RemoteStatus = remote, CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-10), StatusOccurredAtUtc = DateTimeOffset.UtcNow.AddDays(-10) } };
        var window = new OrderWindowDto { Mode = OrderWindowModes.LastHours, Hours = 1, AlwaysShowOpenOrders = true };
        Assert.Equal(visible, TrackingRepository.IsInOrderWindow(order, window, OrderWindowRules.Resolve(window, DateTimeOffset.UtcNow)));
        Assert.Equal("rascunho", order.StatusPedido);
    }

    [Theory]
    [InlineData("location")]
    [InlineData("payment")]
    public void IndependentDataIssueDoesNotPreventAcceptanceButEventConflictDoes(string issue)
    {
        var context = IfoodCommandTests.Context();
        var root = JsonNode.Parse(context.Payload)!;
        if (issue == "location") root["delivery"]!["deliveryAddress"]!["coordinates"] = null;
        else root["payments"]!["pending"] = null;
        context.Payload = root.ToJsonString(); context.SynchronizationState = "review";
        var order = IfoodOrderNormalizer.Normalize(context.Payload, context.MerchantId, context.ExternalOrderId);
        Assert.Single(order.ReviewReasons);
        var options = IfoodCommandTests.Options(context.MerchantId, context.EstabelecimentoId);
        IfoodCommandRules.Validate("confirm", context, 1, new IfoodCommandTests.Clock().Now, options);
        context.HasReviewEvents = true;
        Assert.Equal("IFOOD_COMMAND_REQUIRES_REVIEW", Assert.Throws<DeliveryDomainException>(() =>
            IfoodCommandRules.Validate("confirm", context, 1, new IfoodCommandTests.Clock().Now, options)).Code);
    }

    [Fact]
    public void RecoveredUnknownStateCannotBeAcceptedAgain()
    {
        var context = IfoodCommandTests.Context(); var root = JsonNode.Parse(context.Payload)!;
        root["zippyRecovery"] = true; context.Payload = root.ToJsonString();
        Assert.Equal("IFOOD_RECOVERED_ORDER", Assert.Throws<DeliveryDomainException>(() => IfoodCommandRules.Validate("confirm", context, 1,
            new IfoodCommandTests.Clock().Now, IfoodCommandTests.Options(context.MerchantId, context.EstabelecimentoId))).Code);
    }
}
