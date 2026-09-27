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
}
