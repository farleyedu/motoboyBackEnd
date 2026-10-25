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

    [Fact]
    public void EstablishmentManagerHasEveryActionOfActiveModules()
    {
        var result = EstablishmentPermissions.ResolveForAccess(null,
            new[] { "PEDIDOS", "DELIVERY", "CARDAPIO", "IMPRESSAO" },
            "colaborador", "gerente_estabelecimento");

        Assert.Equal(EstablishmentPermissions.Catalog()["Configuracoes"].Order(), result["Configuracoes"].Order());
        Assert.Equal(EstablishmentPermissions.Catalog()["Cardapio"].Order(), result["Cardapio"].Order());
        Assert.Contains("gestao_motoboy", result["Delivery"]);
        Assert.Contains("configurar_impressao", result["Delivery"]);
        Assert.False(result.ContainsKey("Empresas"));
        Assert.False(result.ContainsKey("Financeiro"));
    }

    [Fact]
    public void CompanyManagerAlsoHasAdministrativePermissions()
    {
        var result = EstablishmentPermissions.ResolveForAccess("{}", new[] { "PEDIDOS" },
            "gerente_empresa", null);

        Assert.Equal(EstablishmentPermissions.Catalog()["Usuarios"].Order(), result["Usuarios"].Order());
        Assert.Equal(EstablishmentPermissions.Catalog()["Empresas"].Order(), result["Empresas"].Order());
        Assert.Equal(EstablishmentPermissions.Catalog()["Estabelecimentos"].Order(), result["Estabelecimentos"].Order());
        Assert.Contains("visualizar", result["Delivery"]);
    }

    [Fact]
    public void ManagerSpecialActionsStillDependOnCommercialModules()
    {
        var result = EstablishmentPermissions.ResolveForAccess(null, new[] { "PEDIDOS" },
            null, "gerente_estabelecimento");

        Assert.Contains("visualizar", result["Delivery"]);
        Assert.DoesNotContain("gestao_motoboy", result["Delivery"]);
        Assert.DoesNotContain("integracao_ifood", result["Delivery"]);
        Assert.DoesNotContain("configurar_impressao", result["Delivery"]);
    }

    [Fact]
    public void CommonUserKeepsCustomizedPermissions()
    {
        var result = EstablishmentPermissions.ResolveForAccess(
            "{\"Delivery\":[\"visualizar\"]}", new[] { "PEDIDOS", "DELIVERY" },
            "colaborador", "atendente");

        Assert.Equal(new[] { "visualizar" }, result["Delivery"]);
        Assert.False(result.ContainsKey("Configuracoes"));
    }

    [Theory]
    [InlineData("gerente_empresa", null)]
    [InlineData("colaborador", "gerente_estabelecimento")]
    public void ManagersCanConnectTheStoreWhatsapp(string companyRole, string? establishmentRole)
    {
        var result = EstablishmentPermissions.ResolveForAccess("{}", new[] { "PEDIDOS", "WHATSAPP" }, companyRole, establishmentRole);

        Assert.Contains("configurar", result["WhatsApp"]);
        Assert.Contains("visualizar", result["WhatsApp"]);
        Assert.Null(EstablishmentPermissions.Validate(new() { ["WhatsApp"] = new() { "configurar" } }, new(), true));
    }
}
