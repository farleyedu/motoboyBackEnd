using APIBack.Service;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class WebOfferPushTests
{
    internal static (string PublicKey, string PrivateKey) Keys()
    {
        using var curve = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = curve.ExportParameters(true);
        var publicKey = new byte[65]; publicKey[0] = 4;
        Buffer.BlockCopy(parameters.Q.X!, 0, publicKey, 1, 32); Buffer.BlockCopy(parameters.Q.Y!, 0, publicKey, 33, 32);
        static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (Encode(publicKey), Encode(parameters.D!));
    }
    private static WebPushRegistration Subscription(string endpoint) => new(endpoint, new(Keys().PublicKey, Convert.ToBase64String(new byte[16])));
    [Theory]
    [InlineData("https://web.push.apple.com/test", true)]
    [InlineData("https://fcm.googleapis.com/wp/test", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/test", true)]
    [InlineData("https://fcm.googleapis.com.evil.invalid/wp/test", false)]
    [InlineData("https://127.0.0.1/push", false)]
    [InlineData("http://fcm.googleapis.com/push", false)]
    [InlineData("https://web.push.apple.com:8443/push", false)]
    [InlineData("https://user@web.push.apple.com/push", false)]
    public void SubscriptionAllowsOnlyProviderEndpoints(string endpoint, bool valid) => Assert.Equal(valid, WebOfferPushService.ValidSubscription(Subscription(endpoint)));
    [Fact]
    public void MalformedEncryptionKeysCannotBeRegistered()
    {
        var valid = Subscription("https://web.push.apple.com/test");
        Assert.False(WebOfferPushService.ValidSubscription(valid with { Keys = new("not-a-key", "!") }));
        Assert.False(WebOfferPushService.ValidSubscription(valid with { Keys = new(valid.Keys.P256dh, Convert.ToBase64String(new byte[8])) }));
    }
}
