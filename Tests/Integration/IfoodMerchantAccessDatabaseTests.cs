using APIBack.Integrations.Ifood;
using Microsoft.Extensions.Options;
using Xunit;

namespace APIBack.Tests.Integration;

public sealed partial class IfoodIntegrationSchemaDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task AuthorizationRequiresExactStoreAndCurrentAuthorizationEvidence()
    {
        await using var db = await Database.Create();
        Assert.False(await db.Connections.IsAuthorizedAsync(db.StoreA, db.MerchantA));
        await db.Execute("UPDATE ifood_connections SET status='authorized',authorized_at_utc=NOW() WHERE merchant_id=@Merchant", new { Merchant = db.MerchantA });
        Assert.True(await db.Connections.IsAuthorizedAsync(db.StoreA, db.MerchantA));
        Assert.False(await db.Connections.IsAuthorizedAsync(db.StoreB, db.MerchantA));
        Assert.False(await db.Connections.IsAuthorizedAsync(db.StoreA, db.MerchantB));
        foreach (var status in new[] { "pending", "revoked", "disabled" })
        {
            await db.Execute("UPDATE ifood_connections SET status=@Status WHERE merchant_id=@Merchant", new { Status = status, Merchant = db.MerchantA });
            Assert.False(await db.Connections.IsAuthorizedAsync(db.StoreA, db.MerchantA));
        }
        await db.Execute("UPDATE ifood_connections SET status='authorized',revoked_at_utc=NOW() WHERE merchant_id=@Merchant", new { Merchant = db.MerchantA });
        Assert.False(await db.Connections.IsAuthorizedAsync(db.StoreA, db.MerchantA));
        await db.Execute("UPDATE ifood_connections SET authorized_at_utc=NULL,revoked_at_utc=NULL WHERE merchant_id=@Merchant", new { Merchant = db.MerchantA });
        Assert.False(await db.Connections.IsAuthorizedAsync(db.StoreA, db.MerchantA));
    }

    [DeliveryDatabaseFact]
    public async Task MerchantGateRechecksDatabaseRevocationAfterTokenRenewal()
    {
        await using var db = await Database.Create();
        await db.Execute("UPDATE ifood_connections SET status='authorized',authorized_at_utc=NOW() WHERE merchant_id=@Merchant", new { Merchant = db.MerchantA });
        var tokens = new RevokingTokens(async () => await db.Execute(
            "UPDATE ifood_connections SET status='revoked',revoked_at_utc=NOW() WHERE merchant_id=@Merchant", new { Merchant = db.MerchantA }));
        var access = new IfoodMerchantAccess(db.Connections, tokens,
            APIBack.Tests.Unit.IfoodAuthenticationTests.Monitor(APIBack.Tests.Unit.IfoodAuthenticationTests.EnabledOptions()));
        Assert.Equal("IFOOD_MERCHANT_NOT_AUTHORIZED", (await Assert.ThrowsAsync<IfoodIntegrationException>(() => access.GetTokenAsync(db.StoreA, db.MerchantA))).Code);
        Assert.Equal(1, tokens.Calls);
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => access.GetTokenAsync(db.StoreA, db.MerchantA));
        Assert.Equal(1, tokens.Calls);
    }

    [DeliveryDatabaseFact]
    public async Task MissingSchemaFailsClosedWithSanitizedDiagnostic()
    {
        await using var db = await Database.Create(apply: false);
        var ex = await Assert.ThrowsAsync<IfoodIntegrationException>(() => db.Connections.IsAuthorizedAsync(db.StoreA, db.MerchantA));
        Assert.Equal("IFOOD_SCHEMA_UNAVAILABLE", ex.Code);
        Assert.Null(ex.InnerException);
        Assert.DoesNotContain("SELECT", ex.ToString());
    }

    private sealed class RevokingTokens(Func<Task> revoke) : IIfoodTokenProvider
    {
        public int Calls;
        public async Task<IfoodAccessToken> GetAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            await revoke();
            return new IfoodAccessToken("test-token", DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromMinutes(5));
        }
        public void Invalidate(IfoodAccessToken rejectedToken) { }
    }
}
