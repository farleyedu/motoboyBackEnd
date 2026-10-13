using System.ComponentModel.DataAnnotations;
using APIBack.Controllers;
using APIBack.DTOs.Common;
using APIBack.DTOs.Experience;
using APIBack.Repository.Interface;
using APIBack.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class ExperienceTests
{
    private static ExperienceLocationDto Location() => new() { Latitude = -18.9186, Longitude = -48.2772, Cidade = " Uberlândia ", Uf = "mg" };

    [Fact]
    public async Task PublicLocationDoesNotRequireAnIdentityAndDoesNotExposeAuditFields()
    {
        var repository = new Mock<IExperienceRepository>();
        repository.Setup(r => r.GetLocationAsync(default)).ReturnsAsync(Location());
        var controller = Controller(repository);
        var response = Assert.IsType<OkObjectResult>(await controller.GetLocation(default));
        var envelope = Assert.IsType<ApiResponse<ExperienceLocationDto>>(response.Value);
        Assert.True(envelope.Success);
        Assert.Equal(-18.9186, envelope.Data!.Latitude);
        Assert.NotEmpty(typeof(ExperienceController).GetMethod(nameof(ExperienceController.GetLocation))!.GetCustomAttributes(typeof(AllowAnonymousAttribute), true));
        Assert.Empty(typeof(ExperienceController).GetMethod(nameof(ExperienceController.SaveLocation))!.GetCustomAttributes(typeof(AllowAnonymousAttribute), true));
    }

    [Fact]
    public async Task SaveRejectsAnonymousAndAuthenticatedRestaurantUsersBeforeWriting()
    {
        var repository = new Mock<IExperienceRepository>(MockBehavior.Strict);
        var controller = Controller(repository);
        Assert.IsType<UnauthorizedObjectResult>(await controller.SaveLocation(Location(), default));
        controller.HttpContext.Items["UserId"] = 7;
        controller.HttpContext.Items["EstabelecimentoNome"] = "Pizzaria";
        controller.HttpContext.Items["Permissoes"] = new Dictionary<string, List<string>> { ["Configuracoes"] = new() { "visualizar", "configurar" } };
        var rejected = Assert.IsType<ObjectResult>(await controller.SaveLocation(Location(), default));
        Assert.Equal(403, rejected.StatusCode);
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false, "ZippyGo Centro", true, true)]
    [InlineData(false, "ZippyGo Centro", false, false)]
    [InlineData(false, "Pizzaria", true, false)]
    [InlineData(true, "Pizzaria", false, true)]
    public void ConfigurationRequiresManagementAccess(bool superAdmin, string name, bool permission, bool expected) =>
        Assert.Equal(expected, ExperienceService.CanConfigure(superAdmin, name, Array.Empty<string>(), permission));

    [Fact]
    public async Task ManagementModulesCanSaveAndNormalizeLocation()
    {
        var repository = new Mock<IExperienceRepository>();
        repository.Setup(r => r.SaveLocationAsync(It.IsAny<ExperienceLocationDto>(), 7, default))
            .ReturnsAsync((ExperienceLocationDto location, int _, CancellationToken _) => location);
        var service = new ExperienceService(repository.Object);
        var saved = await service.SaveLocationAsync(Location(), 7, false, "Gestão",
            new[] { "Empresas", "Estabelecimentos", "Usuarios" }, true, default);
        Assert.Equal("Uberlândia", saved.Cidade);
        Assert.Equal("MG", saved.Uf);
        repository.Verify(r => r.SaveLocationAsync(It.IsAny<ExperienceLocationDto>(), 7, default), Times.Once);
    }

    [Theory]
    [InlineData(null, -48d)]
    [InlineData(-18d, null)]
    [InlineData(86d, -48d)]
    [InlineData(85.1d, -48d)]
    [InlineData(-18d, 181d)]
    [InlineData(-18d, 180.1d)]
    [InlineData(double.NaN, -48d)]
    [InlineData(-18d, double.PositiveInfinity)]
    public async Task InvalidCoordinatesNeverReachPersistence(double? latitude, double? longitude)
    {
        var repository = new Mock<IExperienceRepository>(MockBehavior.Strict);
        var service = new ExperienceService(repository.Object);
        await Assert.ThrowsAsync<ValidationException>(() => service.SaveLocationAsync(
            new() { Latitude = latitude, Longitude = longitude }, 7, true, "", Array.Empty<string>(), false, default));
        repository.VerifyNoOtherCalls();
    }

    private static ExperienceController Controller(Mock<IExperienceRepository> repository) =>
        new(new ExperienceService(repository.Object), NullLogger<ExperienceController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
}
