using System.Security.Cryptography;
using APIBack.Integrations.Ifood;
using Microsoft.Extensions.Options;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class IfoodSecurityTests
{
    private static IfoodSecretProtector Protector(IfoodOptions options) => new(Microsoft.Extensions.Options.Options.Create(options));

    [Fact]
    public void EncryptionAuthenticatesPurposeAndSupportsRotationWithoutLosingOldSecrets()
    {
        var options = IfoodAuthenticationTests.EnabledOptions();
        options.EncryptionKeys["key1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var protector = Protector(options);
        var first = protector.Protect("test-only-secret", "client-secret");
        var second = protector.Protect("test-only-secret", "client-secret");
        Assert.NotEqual(first, second);
        Assert.DoesNotContain("test-only-secret", first);
        Assert.Equal("test-only-secret", protector.Unprotect(first, "client-secret"));
        Assert.Throws<IfoodIntegrationException>(() => protector.Unprotect(first, "access-token"));
        options.EncryptionKeys["key2"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        options.ActiveEncryptionKeyId = "key2";
        var rotated = Protector(options);
        Assert.Equal("test-only-secret", rotated.Unprotect(first, "client-secret"));
        Assert.StartsWith("v1.key2.", rotated.Protect("test-only-secret", "client-secret"));
        var payload = Convert.FromBase64String(first.Split('.')[2]);
        payload[^1] ^= 1;
        Assert.Throws<IfoodIntegrationException>(() => rotated.Unprotect("v1.key1." + Convert.ToBase64String(payload), "client-secret"));
        options.EncryptionKeys.Remove("key1");
        Assert.Equal("IFOOD_SECRET_KEY_UNAVAILABLE", Assert.Throws<IfoodIntegrationException>(() => rotated.Unprotect(first, "client-secret")).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plaintext-secret")]
    [InlineData("v2.key1.invalid")]
    [InlineData("v1.key1.invalid")]
    [InlineData("v1.key1.AA==")]
    public void RejectsPlaintextMalformedOrUnsupportedEnvelopes(string envelope)
    {
        var ex = Assert.Throws<IfoodIntegrationException>(() => Protector(IfoodAuthenticationTests.EnabledOptions()).Unprotect(envelope, "client-secret"));
        Assert.Null(ex.InnerException);
        Assert.DoesNotContain("plaintext-secret", ex.ToString());
    }

    [Fact]
    public void DisabledConfigurationIsOptionalAndEnabledConfigurationFailsWithoutPrintingSecrets()
    {
        var validator = new IfoodOptionsValidator();
        Assert.True(validator.Validate(null, new IfoodOptions()).Succeeded);
        Assert.True(validator.Validate(null, IfoodAuthenticationTests.EnabledOptions()).Succeeded);
        var invalid = IfoodAuthenticationTests.EnabledOptions();
        invalid.ClientSecret = "__SET_IN_ENV__";
        invalid.EncryptionKeys["key1"] = "secret-invalid-key";
        var result = validator.Validate(null, invalid);
        Assert.True(result.Failed);
        Assert.DoesNotContain(invalid.EncryptionKeys["key1"], result.FailureMessage);
        Assert.DoesNotContain(invalid.ClientSecret, invalid.ToString());
        invalid = IfoodAuthenticationTests.EnabledOptions();
        invalid.EncryptionKeys["key1"] = Convert.ToBase64String(new byte[16]);
        Assert.True(validator.Validate(null, invalid).Failed);
        invalid = IfoodAuthenticationTests.EnabledOptions();
        invalid.ActiveEncryptionKeyId = "absent";
        Assert.True(validator.Validate(null, invalid).Failed);
        invalid = IfoodAuthenticationTests.EnabledOptions();
        invalid.HttpTimeoutSeconds = 0;
        Assert.True(validator.Validate(null, invalid).Failed);
    }

    [Fact]
    public async Task MerchantGateRejectsUnauthorizedStoresBeforeTokenAndRevocationDuringRenewal()
    {
        var store = Guid.NewGuid(); var merchant = Guid.NewGuid();
        var connections = new Connections(store, merchant);
        var tokens = new Tokens();
        var access = new IfoodMerchantAccess(connections, tokens, IfoodAuthenticationTests.Monitor(IfoodAuthenticationTests.EnabledOptions()));
        Assert.Equal("IFOOD_MERCHANT_NOT_AUTHORIZED", (await Assert.ThrowsAsync<IfoodIntegrationException>(() => access.GetTokenAsync(Guid.NewGuid(), merchant))).Code);
        Assert.Equal(0, tokens.Calls);
        await access.GetTokenAsync(store, merchant);
        Assert.Equal(1, tokens.Calls);
        tokens.OnGet = () => connections.Authorized = false;
        Assert.Equal("IFOOD_MERCHANT_NOT_AUTHORIZED", (await Assert.ThrowsAsync<IfoodIntegrationException>(() => access.GetTokenAsync(store, merchant))).Code);
        Assert.Equal(2, tokens.Calls);
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => access.GetTokenAsync(store, merchant));
        Assert.Equal(2, tokens.Calls);
    }

    [Fact]
    public async Task DisabledMerchantGateNeverTouchesDatabaseOrToken()
    {
        var connections = new Connections(Guid.NewGuid(), Guid.NewGuid());
        var tokens = new Tokens();
        var access = new IfoodMerchantAccess(connections, tokens, IfoodAuthenticationTests.Monitor(new IfoodOptions()));
        Assert.Equal("IFOOD_DISABLED", (await Assert.ThrowsAsync<IfoodIntegrationException>(() => access.GetTokenAsync(Guid.NewGuid(), Guid.NewGuid()))).Code);
        Assert.Equal(0, connections.Calls);
        Assert.Equal(0, tokens.Calls);
    }

    private sealed class Connections(Guid store, Guid merchant) : IIfoodConnectionRepository
    {
        public bool Authorized = true;
        public int Calls;
        public Task<bool> IsAuthorizedAsync(Guid estabelecimentoId, Guid merchantId, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(Authorized && store == estabelecimentoId && merchant == merchantId); }
    }
    private sealed class Tokens : IIfoodTokenProvider
    {
        public int Calls;
        public Action? OnGet;
        public Task<IfoodAccessToken> GetAsync(CancellationToken cancellationToken = default)
        { Calls++; OnGet?.Invoke(); return Task.FromResult(new IfoodAccessToken("test-token", DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromMinutes(5))); }
        public void Invalidate(IfoodAccessToken rejectedToken) { }
    }
}
