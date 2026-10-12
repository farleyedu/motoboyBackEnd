using APIBack.Model.Auth;
using APIBack.Options;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class AuthDecisionCacheTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static AuthDecisionKey Key() => AuthDecisionKey.From(new JwtPayload {
        UserId = 7, EmpresaId = Guid.NewGuid(), EstabelecimentoId = Guid.NewGuid(),
        MotoboyId = 1, MotoboySessionId = Guid.NewGuid(), SessionEpoch = 3,
        MotoboyLoginSessionId = Guid.NewGuid(), TokenUse = "delivery_operational"
    }, "context:False:False");

    [Fact]
    public async Task PositiveHitExpiresAtTwentySeconds()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var clock = new Clock();
        var cache = new AuthDecisionCache(memory, Microsoft.Extensions.Options.Options.Create(new AuthCacheOptions()), clock);
        var key = Key(); int queries = 0;
        Task<bool> Check() { queries++; return Task.FromResult(true); }
        Assert.True(await cache.IsAllowedAsync(key, Check));
        clock.Now = clock.Now.AddSeconds(19);
        Assert.True(await cache.IsAllowedAsync(key, Check)); Assert.Equal(1, queries);
        clock.Now = clock.Now.AddSeconds(1);
        Assert.True(await cache.IsAllowedAsync(key, Check)); Assert.Equal(2, queries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    public async Task NeverCachesDenialOrException(int ttl)
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new AuthDecisionCache(memory, Microsoft.Extensions.Options.Options.Create(new AuthCacheOptions { TtlSeconds = ttl }));
        var key = Key(); int queries = 0;
        Task<bool> Deny() { queries++; return Task.FromResult(false); }
        Assert.False(await cache.IsAllowedAsync(key, Deny));
        Assert.False(await cache.IsAllowedAsync(key, Deny)); Assert.Equal(2, queries);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.IsAllowedAsync(key, () => throw new InvalidOperationException()));
        Assert.True(await cache.IsAllowedAsync(key, () => Task.FromResult(true)));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("session")]
    [InlineData("login")]
    [InlineData("motoboy")]
    [InlineData("store")]
    [InlineData("company")]
    public async Task RevocationInvalidatesImmediately(string scope)
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new AuthDecisionCache(memory, Microsoft.Extensions.Options.Options.Create(new AuthCacheOptions()));
        var key = Key();
        await cache.IsAllowedAsync(key, () => Task.FromResult(true));
        switch (scope) {
            case "user": cache.InvalidateUser(key.UserId!.Value); break;
            case "session": cache.InvalidateSession(key.MotoboySessionId!.Value); break;
            case "login": cache.InvalidateSession(key.MotoboyLoginSessionId!.Value); break;
            case "motoboy": cache.InvalidateMotoboy(key.MotoboyId!.Value); break;
            case "store": cache.InvalidateEstablishment(key.EstabelecimentoId!.Value); break;
            case "company": cache.InvalidateCompany(key.EmpresaId!.Value); break;
        }
        Assert.False(await cache.IsAllowedAsync(key, () => Task.FromResult(false)));
    }

    [Fact]
    public async Task OldInflightDecisionCannotRestoreRevokedAccess()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new AuthDecisionCache(memory, Microsoft.Extensions.Options.Options.Create(new AuthCacheOptions()));
        var key = Key(); var pending = new TaskCompletionSource<bool>(); int queries = 0;
        var result = cache.IsAllowedAsync(key, () => ++queries == 1 ? pending.Task : Task.FromResult(false));
        cache.InvalidateSession(key.MotoboySessionId!.Value); pending.SetResult(true);
        Assert.False(await result); Assert.Equal(2, queries);
    }

    [Fact]
    public async Task DisabledCacheAndDifferentTokenContextsAlwaysRecheck()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var options = new AuthCacheOptions();
        var cache = new AuthDecisionCache(memory, Microsoft.Extensions.Options.Options.Create(options)); var key = Key();
        await cache.IsAllowedAsync(key, () => Task.FromResult(true));
        foreach (var other in new[] { key with { UserId = 8 }, key with { EstabelecimentoId = Guid.NewGuid() },
            key with { EmpresaId = Guid.NewGuid() }, key with { MotoboySessionId = Guid.NewGuid() },
            key with { SessionEpoch = 4 }, key with { MotoboyLoginSessionId = Guid.NewGuid() },
            key with { MotoboyId = 2 }, key with { TokenUse = "identity" }, key with { Check = "context:True:False" } })
            Assert.False(await cache.IsAllowedAsync(other, () => Task.FromResult(false)));
        options.TtlSeconds = 0;
        Assert.False(await cache.IsAllowedAsync(key, () => Task.FromResult(false)));
    }
}
