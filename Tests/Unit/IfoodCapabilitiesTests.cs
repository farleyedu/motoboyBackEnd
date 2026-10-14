using APIBack.Attributes;
using APIBack.Controllers;
using APIBack.DTOs.Delivery;
using APIBack.Integrations.Ifood.Commands;
using APIBack.Integrations.Ifood.Exceptions;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class IfoodCapabilitiesTests
{
    [Theory]
    [InlineData("confirm", "PLACED", "awaiting_acceptance")]
    [InlineData("start_preparation", "CONFIRMED", "accepted")]
    [InlineData("ready_to_pickup", "CONFIRMED", "preparing")]
    [InlineData("dispatch", "CONFIRMED", "ready")]
    public void CapabilitiesUseCommandRulesAndDoNotAdvanceTheOrder(string command, string remote, string prep)
    {
        var context = IfoodCommandTests.Context(); context.RemoteStatus = remote; context.PreparationState = prep;
        var options = IfoodExceptionTests.Options(context.MerchantId, context.EstabelecimentoId);
        options.CommandsEnabled = true;
        options.CommandMerchants.Add(new() { MerchantId = context.MerchantId, EstabelecimentoId = context.EstabelecimentoId });
        options.AllowedCommands.AddRange(new[] { "confirm", "start_preparation", "ready_to_pickup", "dispatch" });
        var action = IfoodCapabilityRules.Actions(context, options, new IfoodCommandTests.Clock().Now, true, false).Single(a => a.Command == command);
        Assert.True(action.Available); Assert.Null(action.BlockedCode); Assert.Equal(remote, context.RemoteStatus);
        Assert.Equal(prep, context.PreparationState); Assert.Equal(1, context.Revision);
    }

    [Theory]
    [InlineData(false, false, "IFOOD_MERCHANT_NOT_AUTHORIZED")]
    [InlineData(true, true, "IFOOD_COMMAND_ACTIVE_OPERATION")]
    public void AuthorizationAndActiveIntentionBlockEveryOrderAction(bool authorized, bool active, string code)
    {
        var context = IfoodCommandTests.Context();
        var options = IfoodCommandTests.Options(context.MerchantId, context.EstabelecimentoId);
        Assert.All(IfoodCapabilityRules.Actions(context, options, new IfoodCommandTests.Clock().Now, authorized, active), action =>
        { Assert.False(action.Available); Assert.Equal(code, action.BlockedCode); });
    }

    [Theory]
    [InlineData("TAKEOUT", "MERCHANT")]
    [InlineData("DELIVERY", "IFOOD")]
    [InlineData("DINE_IN", "MERCHANT")]
    public void NonMerchantDeliveryNeverOffersDispatch(string type, string carrier)
    {
        var context = IfoodCommandTests.Context(type, carrier); context.RemoteStatus = "CONFIRMED"; context.PreparationState = "ready";
        var options = IfoodCommandTests.Options(context.MerchantId, context.EstabelecimentoId);
        var result = IfoodCapabilityRules.Actions(context, options, new IfoodCommandTests.Clock().Now, true, false);
        Assert.Equal("IFOOD_COMMAND_DISPATCH_NOT_APPLICABLE", result.Single(a => a.Command == "dispatch").BlockedCode);
    }

    [Fact]
    public void DisabledActionsAndExpiredConfirmationHaveActionableCodes()
    {
        var context = IfoodCommandTests.Context(); var options = IfoodCommandTests.Options(context.MerchantId, context.EstabelecimentoId);
        var now = new IfoodCommandTests.Clock().Now;
        Assert.Equal("IFOOD_COMMAND_CONFIRMATION_DEADLINE", IfoodCapabilityRules.Actions(context, options, now.AddMinutes(10), true, false).Single(a => a.Command == "confirm").BlockedCode);
        options.CommandsEnabled = false;
        Assert.All(IfoodCapabilityRules.Actions(context, options, now, true, false).Where(a => a.Command != "request_cancellation"), a => Assert.Equal("IFOOD_COMMANDS_DISABLED", a.BlockedCode));
    }

    [Fact]
    public void FiltersValidateExternalModesPreparationAndKeepLeadingZeroes()
    {
        var filter = PedidoFiltro.From(new() { Busca = "#001234", Modalidade = "takeout,DELIVERY", Preparo = "READY" });
        Assert.Equal("001234", filter.BuscaDigitos); Assert.Equal(new[] { "TAKEOUT", "DELIVERY" }, filter.Modalidade);
        Assert.Equal(new[] { "ready" }, filter.Preparo);
        Assert.Throws<DeliveryDomainException>(() => PedidoFiltro.From(new() { Modalidade = "inventado" }));
        Assert.Throws<DeliveryDomainException>(() => PedidoFiltro.From(new() { Preparo = "confirmado" }));
    }

    [Fact]
    public void CapabilitiesEndpointRequiresIntegrationPermission()
    {
        var method = typeof(IfoodCommandsController).GetMethod(nameof(IfoodCommandsController.Capabilities))!;
        var permission = Assert.IsType<RequirePermissionAttribute>(Attribute.GetCustomAttribute(method, typeof(RequirePermissionAttribute)));
        Assert.Equal("Delivery", permission.Modulo); Assert.Equal("integracao_ifood", permission.Acao);
    }
}
