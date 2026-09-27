using APIBack.Security;
using Xunit;

namespace APIBack.Tests.Unit;

public class EstablishmentPermissionsTests
{
    [Fact]
    public void InactiveModulesAreDeniedButAdministrationRemains()
    {
        var result = EstablishmentPermissions.Resolve("{\"Delivery\":[\"visualizar\"],\"Configuracoes\":[\"visualizar\"]}", new[] { "WHATSAPP" });
        Assert.False(result.ContainsKey("Delivery"));
        Assert.Contains("visualizar", result["Configuracoes"]);
    }

    [Fact]
    public void ExplicitRevocationDoesNotInheritCatalogPermissions()
    {
        var result = EstablishmentPermissions.Resolve("{\"Configuracoes\":[\"editar\"],\"Cardapio\":[],\"CardapioWeb\":[]}", new[] { "CARDAPIO", "CARDAPIOWEB" });
        Assert.Empty(result["Cardapio"]);
        Assert.Empty(result["CardapioWeb"]);
    }

    [Fact]
    public void ActorCannotGrantPrivilegesTheyDoNotHave()
    {
        var requested = new Dictionary<string, List<string>> { ["Configuracoes"] = new() { "configurar" } };
        Assert.NotNull(EstablishmentPermissions.Validate(requested, new(), false));
        Assert.Null(EstablishmentPermissions.Validate(requested, new(), true));
        Assert.Null(EstablishmentPermissions.Validate(new(), new(), false));
    }

    [Fact]
    public void InvalidActionIsRejectedEvenForSuperAdmin()
    {
        Assert.NotNull(EstablishmentPermissions.Validate(new() { ["Financeiro"] = new() { "inventada" } }, new(), true));
    }
}
