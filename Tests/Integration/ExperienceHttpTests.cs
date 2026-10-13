using System.Net;
using System.Net.Http.Json;
using APIBack.Controllers;
using APIBack.DTOs.Common;
using APIBack.DTOs.Experience;
using APIBack.Repository.Interface;
using APIBack.Service;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Moq;
using Xunit;

namespace APIBack.Tests.Integration;

public sealed class ExperienceHttpTests
{
    [Fact]
    public async Task PublicReadAndManagementWriteFollowTheActualMvcAuthorizationFilters()
    {
        ExperienceLocationDto? stored = null;
        var repository = new Mock<IExperienceRepository>();
        repository.Setup(r => r.GetLocationAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => stored);
        repository.Setup(r => r.SaveLocationAsync(It.IsAny<ExperienceLocationDto>(), 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExperienceLocationDto location, int _, CancellationToken _) => stored = location);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new ExperienceService(repository.Object));
        builder.Services.AddControllers(options => options.Filters.Add(new APIBack.Attributes.AuthorizeAttribute()))
            .AddApplicationPart(typeof(ExperienceController).Assembly);
        await using var app = builder.Build();
        // Identidades controladas pelo teste: nenhuma sessão ou credencial real.
        app.Use(async (context, next) =>
        {
            var actor = context.Request.Headers["X-Test-Actor"].ToString();
            if (!string.IsNullOrEmpty(actor))
            {
                context.Items["UserId"] = 7;
                context.Items["EstabelecimentoNome"] = actor == "management" ? "ZippyGo Centro" : "Pizzaria";
                context.Items["Permissoes"] = new Dictionary<string, List<string>> { ["Configuracoes"] = new() { "visualizar" } };
            }
            await next();
        });
        app.MapControllers();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var location = new ExperienceLocationDto { Latitude = -18.9186, Longitude = -48.2772, Cidade = "Uberlândia", Uf = "mg" };
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/experience/location")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync("/api/gestao/experience/location", location)).StatusCode);
            client.DefaultRequestHeaders.Add("X-Test-Actor", "restaurant");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/gestao/experience/location", location)).StatusCode);
            Assert.Null(stored);
            client.DefaultRequestHeaders.Remove("X-Test-Actor");
            client.DefaultRequestHeaders.Add("X-Test-Actor", "management");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/gestao/experience/location", new { latitude = -18.0 })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/gestao/experience/location", location)).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Test-Actor");
            var publicLocation = await client.GetFromJsonAsync<ApiResponse<ExperienceLocationDto>>("/api/experience/location");
            Assert.Equal("MG", publicLocation!.Data!.Uf);
            Assert.Equal(location.Latitude, publicLocation.Data.Latitude);
            repository.Verify(r => r.SaveLocationAsync(It.IsAny<ExperienceLocationDto>(), 7, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { await app.StopAsync(); }
    }
}
