using APIBack.DTOs.Delivery;
using APIBack.Repository;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class DisconnectIncidentRulesTests
{
    private static IncidenteAcaoDisponivelDto Item(string action, bool reconnected, bool collected, string state, bool exceptional = false) =>
        DisconnectIncidentRules.ItemActions(reconnected, collected, state, exceptional).Single(a => a.Acao == action);

    [Fact]
    public void OrderNotPickedUpIsAtTheStoreAndCanBeRedistributedOrCancelled()
    {
        Assert.True(Item(IncidentActions.Redistribuir, false, false, "pendente").Permitida);
        Assert.True(Item(IncidentActions.Cancelar, false, false, "pendente").Permitida);
        Assert.False(Item(IncidentActions.ConfirmarDevolucaoFisica, false, false, "pendente").Permitida);
        Assert.False(Item(IncidentActions.RegistrarFalha, false, false, "pendente").Permitida);
    }

    [Fact]
    public void CollectedOrderIsNotRedistributedUntilPhysicalReturnIsConfirmed()
    {
        var redistribute = Item(IncidentActions.Redistribuir, true, true, "pendente", exceptional: true);
        Assert.False(redistribute.Permitida);
        Assert.False(string.IsNullOrWhiteSpace(redistribute.MotivoBloqueio));
        Assert.True(Item(IncidentActions.ConfirmarDevolucaoFisica, false, true, "pendente").Permitida);
        Assert.True(Item(IncidentActions.Redistribuir, false, true, "devolvido").Permitida);
        Assert.False(Item(IncidentActions.ConfirmarDevolucaoFisica, false, true, "devolvido").Permitida);
    }

    [Fact]
    public void CancelAndFailureOfGoodsStillWithTheCourierNeedExceptionalAuthorization()
    {
        Assert.False(Item(IncidentActions.Cancelar, false, true, "pendente").Permitida);
        Assert.True(Item(IncidentActions.Cancelar, false, true, "pendente", exceptional: true).Permitida);
        Assert.False(Item(IncidentActions.RegistrarFalha, false, true, "pendente").Permitida);
        Assert.True(Item(IncidentActions.RegistrarFalha, false, true, "pendente", exceptional: true).Permitida);
        Assert.True(Item(IncidentActions.RegistrarFalha, false, true, "devolvido").Permitida);
    }

    [Fact]
    public void ReofferNeedsTheCourierToHaveReconnected()
    {
        Assert.False(Item(IncidentActions.ReofertarMesmoMotoboy, false, false, "pendente").Permitida);
        Assert.True(Item(IncidentActions.ReofertarMesmoMotoboy, true, false, "pendente").Permitida);
        Assert.True(Item(IncidentActions.ReofertarMesmoMotoboy, true, true, "pendente").Permitida);
    }

    [Fact]
    public void ManualCompletionAndCancellationAlwaysAskForJustification()
    {
        foreach (var action in new[] { IncidentActions.ConcluirManual, IncidentActions.Cancelar, IncidentActions.RegistrarFalha })
            Assert.True(Item(action, true, false, "devolvido", exceptional: true).ExigeJustificativa);
        Assert.False(Item(IncidentActions.Redistribuir, true, false, "pendente").ExigeJustificativa);
    }

    [Fact]
    public void ResolvedOrderAcceptsNoAction()
    {
        Assert.All(DisconnectIncidentRules.ItemActions(true, false, "resolvido", true), a => Assert.False(a.Permitida));
    }

    [Fact]
    public void IncidentLevelActionsFollowTheIncidentStatus()
    {
        Assert.True(DisconnectIncidentRules.LevelActions("aberto").Single(a => a.Acao == IncidentActions.AguardarContato).Permitida);
        Assert.False(DisconnectIncidentRules.LevelActions("aguardando_retorno").Single(a => a.Acao == IncidentActions.AguardarContato).Permitida);
        Assert.True(DisconnectIncidentRules.LevelActions("aguardando_retorno").Single(a => a.Acao == IncidentActions.RegistrarContato).Permitida);
        Assert.All(DisconnectIncidentRules.LevelActions("resolvido"), a => Assert.False(a.Permitida));
    }

    [Theory]
    [InlineData("inactivity_7h")]
    [InlineData("client_end")]
    [InlineData("login_replaced")]
    [InlineData("link_revoked")]
    public void EveryRealEndReasonHasHumanText(string reason) => Assert.False(string.IsNullOrWhiteSpace(DisconnectIncidentRules.ReasonText(reason)));
}
