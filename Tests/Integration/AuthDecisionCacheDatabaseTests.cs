using APIBack.Middleware;
using APIBack.Model.Auth;
using APIBack.Options;
using APIBack.Repository;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace APIBack.Tests.Integration;

public partial class DeliverySyncDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task CachedMiddlewareChecksTenantPauseAndExplicitEndAndRecoversOnlyOpenPresence()
    {
        await using var db = await ResumeDatabase();
        var company = Guid.NewGuid();
        await db.Execute("""
ALTER TABLE estabelecimentos ADD COLUMN id_empresa uuid;
CREATE TABLE empresas(id uuid PRIMARY KEY,ativo bool,pausada bool);
INSERT INTO empresas VALUES(@Company,TRUE,FALSE);
UPDATE estabelecimentos SET id_empresa=@Company;
UPDATE delivery_route_stops SET stop_status='cancelled';
""", new { Company = company });
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new AuthDecisionCache(memory, Microsoft.Extensions.Options.Options.Create(new AuthCacheOptions()));
        var repository = new OperationalSessionRepository(db.Source,
            Microsoft.Extensions.Options.Options.Create(new DeliveryTrackingOptions()), authCache: cache);
        using var services = new ServiceCollection().AddSingleton(db.Source).AddSingleton<IAuthDecisionCache>(cache).BuildServiceProvider();
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.ValidateToken("a.b.c")).Returns(() => new JwtPayload {
            UserId = 7, EstabelecimentoId = db.StoreId, MotoboyId = 1, MotoboySessionId = db.SessionId,
            SessionEpoch = 4, TokenUse = "delivery_operational"
        });
        async Task<bool> Allowed(string path = "/api/v2/motoboys/me/session/queue", string method = "GET")
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Headers.Authorization = "Bearer a.b.c";
            context.Request.Path = path; context.Request.Method = method;
            await new JwtAuthenticationMiddleware(_ => Task.CompletedTask).InvokeAsync(context, jwt.Object, new ConfigurationBuilder().Build());
            return context.Items.ContainsKey("JwtPayload");
        }
        Assert.True(await Allowed()); Assert.True(await Allowed());
        await db.Execute("UPDATE empresas SET pausada=TRUE"); cache.InvalidateCompany(company);
        Assert.False(await Allowed());
        await db.Execute("UPDATE empresas SET pausada=FALSE; UPDATE motoboy_active_sessions SET expires_at_utc=NOW()-INTERVAL '1 second'");
        Assert.False(await Allowed());
        Assert.True(await Allowed("/api/v2/motoboys/me/session/heartbeat", "POST"));
        Assert.False(await Allowed());
        await repository.HeartbeatAsync(db.SessionId, 1, 4);
        Assert.True(await Allowed());
        await repository.EndSessionAsync(db.SessionId, 1, 4, "client_end");
        Assert.False(await Allowed());
        Assert.False(await Allowed("/api/v2/motoboys/me/session/heartbeat", "POST"));
        Assert.True(await Allowed("/api/v2/motoboys/me/session", "DELETE"));
    }
}
