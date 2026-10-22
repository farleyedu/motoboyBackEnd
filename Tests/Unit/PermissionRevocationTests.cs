using APIBack.Automation.Models;
using APIBack.Automation.Repository.Interface;
using APIBack.Extensions;
using APIBack.Middleware;
using APIBack.Model.Auth;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit;

public class PermissionRevocationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OldTokenCannotKeepRevokedPermissionOrCrossStoreMembership(bool inactive, bool differentStore)
    {
        var store = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        var payload = new JwtPayload { UserId = 12, EstabelecimentoId = store, VinculoId = membershipId,
            Permissoes = new() { ["Delivery"] = new() { "visualizar", "editar_pedido" } } };
        var jwt = new Mock<IJwtService>();
        jwt.Setup(x => x.ValidateToken("a.b.c")).Returns(payload);
        var repository = new Mock<IEstabelecimentoSelectionRepository>();
        repository.Setup(x => x.ObterUsuarioAsync(12)).ReturnsAsync(new UsuarioTenant { Id = 12 });
        repository.Setup(x => x.ObterVinculoPorIdAsync(membershipId)).ReturnsAsync(new UsuarioEstabelecimentoAcesso {
            Id = membershipId, UsuarioId = 12, EstabelecimentoId = differentStore ? Guid.NewGuid() : store,
            Status = "ativo", VinculoAtivo = !inactive, TipoAcesso = "atendente",
            PermissoesCustomizadas = "{\"Delivery\":[\"visualizar\"]}" });
        repository.Setup(x => x.ObterEstabelecimentoDetalheAsync(store)).ReturnsAsync(new EstabelecimentoDetalhe {
            Id = store, Ativo = true, ModulosAtivosRaw = new[] { "DELIVERY" } });
        using var services = new ServiceCollection().AddSingleton(repository.Object).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Headers.Authorization = "Bearer a.b.c";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:DefaultConnection"] = "Host=not-used" }).Build();
        await new JwtAuthenticationMiddleware(_ => Task.CompletedTask).InvokeAsync(context, jwt.Object, config);
        Assert.False(context.TemPermissao("Delivery", "editar_pedido"));
        Assert.Equal(!inactive && !differentStore, context.TemPermissao("Delivery", "visualizar"));
    }

    // O /api/auth/me passa por aqui: o gerente nao pode perder Configuracoes logo depois do login (o menu piscava e sumia).
    [Theory]
    [InlineData("gerente_estabelecimento", null, true)]
    [InlineData("atendente", "gerente_empresa", true)]
    [InlineData("atendente", null, false)]
    public async Task EveryRequestResolvesPermissionsWithTheSameManagerRuleAsLogin(string storeRole, string? companyRole, bool settings)
    {
        var store = Guid.NewGuid(); var company = Guid.NewGuid(); var membershipId = Guid.NewGuid();
        var payload = new JwtPayload { UserId = 12, EstabelecimentoId = store, VinculoId = membershipId };
        var jwt = new Mock<IJwtService>();
        jwt.Setup(x => x.ValidateToken("a.b.c")).Returns(payload);
        var repository = new Mock<IEstabelecimentoSelectionRepository>();
        repository.Setup(x => x.ObterUsuarioAsync(12)).ReturnsAsync(new UsuarioTenant { Id = 12 });
        repository.Setup(x => x.ObterVinculoPorIdAsync(membershipId)).ReturnsAsync(new UsuarioEstabelecimentoAcesso {
            Id = membershipId, UsuarioId = 12, EstabelecimentoId = store, Status = "ativo", VinculoAtivo = true,
            TipoAcesso = storeRole, PermissoesCustomizadas = "{\"Delivery\":[\"visualizar\"]}" });
        repository.Setup(x => x.ObterEstabelecimentoDetalheAsync(store)).ReturnsAsync(new EstabelecimentoDetalhe {
            Id = store, EmpresaId = company, Ativo = true, ModulosAtivosRaw = new[] { "PEDIDOS", "DELIVERY" } });
        repository.Setup(x => x.ObterVinculoEmpresaAsync(12, company)).ReturnsAsync(companyRole == null ? null
            : new UsuarioEmpresaAcesso { EmpresaId = company, UsuarioId = 12, Status = "ativo", VinculoAtivo = true, TipoAcesso = companyRole });
        using var services = new ServiceCollection().AddSingleton(repository.Object).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Headers.Authorization = "Bearer a.b.c";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:DefaultConnection"] = "Host=not-used" }).Build();
        await new JwtAuthenticationMiddleware(_ => Task.CompletedTask).InvokeAsync(context, jwt.Object, config);
        Assert.Equal(settings, context.TemPermissao("Configuracoes", "visualizar"));
        Assert.True(context.TemPermissao("Delivery", "visualizar"));
    }
}
