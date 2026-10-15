using APIBack.Controllers;
using APIBack.DTOs.Common;
using APIBack.DTOs.Delivery;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Config;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class IfoodConfigSecretTests
{
    [Fact]
    public async Task GlobalConfigNeverReturnsStoredSecretEvenToSuperadmin()
    {
        var repo = new Mock<IIfoodRuntimeSettingsRepository>();
        repo.Setup(x => x.GetSettingsAsync(default)).ReturnsAsync(new IfoodRuntimeSettings { ClientId = "client-test", ClientSecretCifrado = "stored-envelope-test" });
        var context = new DefaultHttpContext(); context.Items["IsSuperAdmin"] = true;
        var controller = new IfoodConfigController(repo.Object, new IfoodSecretProtector(Microsoft.Extensions.Options.Options.Create(new IfoodOptions()))) {
            ControllerContext = new ControllerContext { HttpContext = context } };
        var response = Assert.IsType<OkObjectResult>(await controller.Get(default));
        var dto = Assert.IsType<ApiResponse<IfoodRuntimeSettingsDto>>(response.Value).Data!;
        Assert.Null(dto.ClientSecret); Assert.Equal("client-test", dto.ClientId);
        context.Items["IsSuperAdmin"] = false;
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.Get(default)).StatusCode);
    }
}
