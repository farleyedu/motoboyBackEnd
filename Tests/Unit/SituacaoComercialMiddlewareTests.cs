using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Middleware;
using APIBack.Service;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit;

public class SituacaoComercialMiddlewareTests
{
    private static readonly Guid Store = Guid.NewGuid();

    [Theory]
    [InlineData("/api/v2/delivery/pedidos", "GET", "bloqueado", "ESTABELECIMENTO_BLOQUEADO")]
    [InlineData("/hubs/delivery/negotiate", "POST", "bloqueado", "ESTABELECIMENTO_BLOQUEADO")]
    [InlineData("/api/v2/motoboys/me/session/start", "POST", "bloqueado", "ESTABELECIMENTO_BLOQUEADO")]
    [InlineData("/api/v2/delivery/pedidos", "GET", "implantacao", "OPERACAO_NAO_INICIADA")]
    public async Task Bloqueia_operacao_no_servidor(string path, string method, string situacao, string code)
    {
        var (context, nextCalled) = await Run(path, method, situacao);
        Assert.False(nextCalled);
        Assert.Equal(403, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("/api/auth/me", "GET")]
    [InlineData("/api/auth/definir-estabelecimento", "POST")]
    [InlineData("/api/me/estabelecimentos", "GET")]
    [InlineData("/api/v2/motoboys/me/session/stops/current/arrive", "POST")]
    [InlineData("/api/v2/motoboys/me/session/completion/42/code", "POST")]
    public async Task Mantem_acesso_estritamente_necessario(string path, string method)
    {
        var (_, nextCalled) = await Run(path, method, SituacoesComerciais.Bloqueado);
        Assert.True(nextCalled);
    }

    private static async Task<(DefaultHttpContext Context, bool NextCalled)> Run(string path, string method, string situacao)
    {
        var context = new DefaultHttpContext();
        context.Items["EstabelecimentoId"] = Store;
        context.Request.Path = path;
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        var nextCalled = false;
        var middleware = new SituacaoComercialMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var service = new Mock<ISituacaoComercialService>();
        service.Setup(s => s.ObterAsync(Store, context.RequestAborted))
            .ReturnsAsync(new SituacaoComercialDto(Store, situacao, null, null, null, 3, 3, 3, 10, null, null));
        await middleware.InvokeAsync(context, service.Object);
        return (context, nextCalled);
    }
}
