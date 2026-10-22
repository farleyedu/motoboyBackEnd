using System.Net;
using System.Text.Json;
using APIBack.Controllers;
using APIBack.DTOs.Common;
using APIBack.DTOs.Delivery;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Config;
using APIBack.Integrations.Ifood.Connections;
using APIBack.Integrations.Ifood.Merchants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class IfoodStoreIdentifierTests
{
    [Theory]
    [InlineData("11.222.333/0001-81")]
    [InlineData("11222333000181")]
    [InlineData("  11 222 333 0001 81 ")]
    public void ValidCnpjIsAcceptedWithOrWithoutMask(string input)
    {
        var parsed = IfoodStoreIdentifier.Parse(input);
        Assert.Equal(IfoodIdentifierKind.Cnpj, parsed.Kind);
        Assert.Equal("11222333000181", parsed.CnpjDigits);
    }

    [Fact]
    public void MerchantUuidIsRecognizedAndTrimmed()
    {
        var id = Guid.NewGuid();
        var parsed = IfoodStoreIdentifier.Parse($"  {id}  ");
        Assert.Equal(IfoodIdentifierKind.MerchantId, parsed.Kind);
        Assert.Equal(id, parsed.MerchantId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("11222333000182")]            // digito verificador errado
    [InlineData("11111111111111")]            // repetidos
    [InlineData("1122233300018")]             // curto
    [InlineData("abc")]
    [InlineData("11.222.333/0001-81x")]
    public void InvalidInputIsRejected(string? input) => Assert.Equal(IfoodIdentifierKind.Invalid, IfoodStoreIdentifier.Parse(input).Kind);
}

public sealed class IfoodConnectionStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static IfoodConnectionRow Row(Action<IfoodConnectionRow>? edit = null)
    {
        var row = new IfoodConnectionRow { MerchantId = Guid.NewGuid(), EstabelecimentoId = Guid.NewGuid(), Status = "authorized", AuthorizedAtUtc = Now.AddDays(-1) };
        edit?.Invoke(row);
        return row;
    }

    [Fact]
    public void LocalAuthorizedStatusAloneIsNeverOperational()
        => Assert.Equal("unverified", IfoodConnectionState.Describe(Row(), Now).State);

    [Fact]
    public void ForbiddenPollingMeansNotOperationalEvenWhenTheMerchantEndpointAnswered()
    {
        var view = IfoodConnectionState.Describe(Row(r => { r.RemoteCheckCode = "AUTHORIZED"; r.PollState = "forbidden"; r.PollBlockedUntilUtc = Now.AddMinutes(4); }), Now);
        Assert.Equal("polling_forbidden", view.State);
        Assert.Contains("recheck", view.Actions);
        Assert.Contains("4 min", view.Message);
    }

    [Fact]
    public void RecentSuccessfulPollIsOperationalAndStaleOneIsNot()
    {
        Assert.Equal("operational", IfoodConnectionState.Describe(Row(r => { r.PollState = "ok"; r.LastPollOkAtUtc = Now.AddSeconds(-40); }), Now).State);
        Assert.Equal("polling_stale", IfoodConnectionState.Describe(Row(r => { r.PollState = "ok"; r.LastPollOkAtUtc = Now.AddMinutes(-10); }), Now).State);
    }

    [Fact]
    public void PendingRevokedAndNotAuthorizedAtIfoodAreExplained()
    {
        Assert.Equal("awaiting_authorization", IfoodConnectionState.Describe(Row(r => r.Status = "pending"), Now).State);
        Assert.Equal("disconnected", IfoodConnectionState.Describe(Row(r => { r.Status = "revoked"; r.RevokedAtUtc = Now; }), Now).State);
        Assert.Equal("not_authorized_at_ifood", IfoodConnectionState.Describe(Row(r => r.RemoteCheckCode = "NOT_AUTHORIZED"), Now).State);
        Assert.Equal("polling_error", IfoodConnectionState.Describe(Row(r => { r.PollState = "error"; r.PollErrorCode = "IFOOD_EVENTS_TIMEOUT"; }), Now).State);
    }
}

public sealed class IfoodMerchantDirectoryTests
{
    private const string Cnpj = "11222333000181";
    private static readonly Guid Wrong = Guid.NewGuid(), Right = Guid.NewGuid();

    private sealed class Factory(IfoodAuthenticationTests.Handler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false); }

    private static IfoodMerchantDirectory Directory(IfoodAuthenticationTests.Handler handler)
    {
        var tokens = new Mock<IIfoodTokenProvider>();
        tokens.Setup(t => t.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new IfoodAccessToken("t", DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromHours(1)));
        return new IfoodMerchantDirectory(new Factory(handler), tokens.Object, IfoodAuthenticationTests.Monitor());
    }
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };

    [Fact]
    public async Task FindsTheMerchantWhoseDocumentMatchesTheCnpj()
    {
        var handler = new IfoodAuthenticationTests.Handler
        {
            Send = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                var p when p.EndsWith("/merchants") => Json($"[{{\"id\":\"{Wrong}\",\"name\":\"A\"}},{{\"id\":\"{Right}\",\"name\":\"B\"}}]"),
                var p when p.EndsWith(Wrong.ToString()) => Json("{\"name\":\"A\",\"documents\":{\"CNPJ\":{\"value\":\"99.999.999/0001-91\"}}}"),
                var p when p.EndsWith(Right.ToString()) => Json("{\"name\":\"B\",\"documents\":{\"CNPJ\":{\"value\":\"11.222.333/0001-81\"}}}"),
                _ => Json("", HttpStatusCode.NotFound)
            })
        };
        var lookup = await Directory(handler).FindByCnpjAsync(Cnpj, default);
        Assert.True(lookup.Found);
        Assert.Equal(Right, lookup.MerchantId);
    }

    [Fact]
    public async Task NeverGuessesWhenNoMerchantExposesThatCnpj()
    {
        var handler = new IfoodAuthenticationTests.Handler
        {
            Send = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/merchants")
                ? Json($"[{{\"id\":\"{Wrong}\"}}]")
                : Json("{\"name\":\"Loja 11222333000181\",\"address\":{\"street\":\"11222333000181\"}}"))   // nome/endereco parecidos nao valem
        };
        var lookup = await Directory(handler).FindByCnpjAsync(Cnpj, default);
        Assert.Equal("NOT_FOUND", lookup.Code);
        Assert.Equal(Guid.Empty, lookup.MerchantId);
    }

    [Theory]
    [InlineData(401, "APP_CREDENTIALS")]
    [InlineData(429, "RATE_LIMIT")]
    [InlineData(500, "UNAVAILABLE")]
    public async Task IfoodFailuresAreReportedNotTreatedAsNotFound(int status, string code)
    {
        var handler = new IfoodAuthenticationTests.Handler { Send = (_, _) => Task.FromResult(Json("", (HttpStatusCode)status)) };
        Assert.Equal(code, (await Directory(handler).FindByCnpjAsync(Cnpj, default)).Code);
    }

    [Fact]
    public async Task ForbiddenDetailOfOneMerchantDoesNotStopTheSearch()
    {
        var handler = new IfoodAuthenticationTests.Handler
        {
            Send = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                var p when p.EndsWith("/merchants") => Json($"[{{\"id\":\"{Wrong}\"}},{{\"id\":\"{Right}\"}}]"),
                var p when p.EndsWith(Wrong.ToString()) => Json("", HttpStatusCode.Forbidden),
                _ => Json("{\"cnpj\":\"11222333000181\"}")
            })
        };
        Assert.Equal(Right, (await Directory(handler).FindByCnpjAsync(Cnpj, default)).MerchantId);
    }

    [Fact]
    public void ListAcceptsWrappedArraysAndIgnoresGarbage()
    {
        Assert.Equal(new[] { Right }, IfoodMerchantDirectory.ListMerchants($"{{\"elements\":[{{\"id\":\"{Right}\"}},{{\"id\":\"x\"}},{{}}]}}").ToArray());
        Assert.Empty(IfoodMerchantDirectory.ListMerchants("not-json"));
    }
}

public sealed class IfoodStoreConnectByCnpjTests
{
    private static readonly Guid Store = Guid.NewGuid(), Merchant = Guid.NewGuid();
    private const string Cnpj = "11.222.333/0001-81";

    private sealed class Rig
    {
        public readonly Mock<IIfoodRuntimeSettingsRepository> Repo = new();
        public readonly Mock<IIfoodMerchantDirectory> Directory = new();
        public readonly Mock<IIfoodConnectionLifecycle> Lifecycle = new();
        public readonly IfoodStoreController Controller;
        public Rig()
        {
            var context = new DefaultHttpContext();
            context.Items["EstabelecimentoId"] = Store;
            Controller = new IfoodStoreController(Repo.Object, new Mock<IIfoodMerchantProbe>().Object, Directory.Object,
                new Mock<IIfoodConnectionHealth>().Object, Lifecycle.Object, TimeProvider.System)
            { ControllerContext = new ControllerContext { HttpContext = context } };
        }
    }

    [Fact]
    public async Task CnpjIsResolvedToTheMerchantAndLinkedAsPendingToTheCallersStoreOnly()
    {
        var rig = new Rig();
        rig.Lifecycle.Setup(l => l.GetStoreCnpjAsync(Store, It.IsAny<CancellationToken>())).ReturnsAsync("11222333000181");
        rig.Directory.Setup(d => d.FindByCnpjAsync("11222333000181", It.IsAny<CancellationToken>())).ReturnsAsync(new IfoodMerchantLookup("FOUND", Merchant, ""));

        var result = Assert.IsType<OkObjectResult>(await rig.Controller.Connect(new IfoodStoreConnectionRequest { Identifier = Cnpj }, default));

        var body = Assert.IsType<ApiResponse<IfoodStoreConnectResultDto>>(result.Value).Data!;
        Assert.Equal(Merchant, body.MerchantId); Assert.True(body.ResolvedFromCnpj);
        rig.Repo.Verify(r => r.RequestStoreConnectionAsync(Merchant, Store, It.IsAny<CancellationToken>()), Times.Once());
        rig.Repo.Verify(r => r.AuthorizeConnectionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task CnpjOfAnotherCompanyIsRefusedBeforeAnyLookup()
    {
        var rig = new Rig();
        rig.Lifecycle.Setup(l => l.GetStoreCnpjAsync(Store, It.IsAny<CancellationToken>())).ReturnsAsync("99999999000191");

        Assert.IsType<ConflictObjectResult>(await rig.Controller.Connect(new IfoodStoreConnectionRequest { Identifier = Cnpj }, default));

        rig.Directory.Verify(d => d.FindByCnpjAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        rig.Repo.Verify(r => r.RequestStoreConnectionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task UnknownCnpjLinksNothing()
    {
        var rig = new Rig();
        rig.Directory.Setup(d => d.FindByCnpjAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new IfoodMerchantLookup("NOT_FOUND", Guid.Empty, "x"));

        var result = Assert.IsType<UnprocessableEntityObjectResult>(await rig.Controller.Connect(new IfoodStoreConnectionRequest { Identifier = Cnpj }, default));

        Assert.Equal("IFOOD_CNPJ_NOT_FOUND", Assert.IsType<ApiResponse<object>>(result.Value).Code);
        rig.Repo.Verify(r => r.RequestStoreConnectionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task IfoodOutageDuringLookupIsReportedAsUnavailableNotAsMissing()
    {
        var rig = new Rig();
        rig.Directory.Setup(d => d.FindByCnpjAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new IfoodMerchantLookup("UNAVAILABLE", Guid.Empty, "fora"));
        Assert.Equal(503, Assert.IsType<ObjectResult>(await rig.Controller.Connect(new IfoodStoreConnectionRequest { Identifier = Cnpj }, default)).StatusCode);
    }

    [Fact]
    public async Task MerchantIdStillWorksAndIsNotLookedUp()
    {
        var rig = new Rig();
        Assert.IsType<OkObjectResult>(await rig.Controller.Connect(new IfoodStoreConnectionRequest { Identifier = Merchant.ToString() }, default));
        rig.Repo.Verify(r => r.RequestStoreConnectionAsync(Merchant, Store, It.IsAny<CancellationToken>()), Times.Once());
        rig.Directory.Verify(d => d.FindByCnpjAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Theory]
    [InlineData("nao-e-nada")]
    [InlineData("11222333000182")]
    public async Task GarbageIsRejectedWithAClearMessage(string input)
    {
        var rig = new Rig();
        var result = Assert.IsType<BadRequestObjectResult>(await rig.Controller.Connect(new IfoodStoreConnectionRequest { Identifier = input }, default));
        Assert.Contains("CNPJ", Assert.IsType<ApiResponse<object>>(result.Value).Error);
    }

    [Fact]
    public async Task CompetingStoreGets409WhenMerchantIsActiveElsewhere()
    {
        var rig = new Rig();
        rig.Repo.Setup(r => r.RequestStoreConnectionAsync(Merchant, Store, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IfoodIntegrationException("IFOOD_CONNECTION_MERCHANT_LINKED_ELSEWHERE"));
        Assert.Equal(409, Assert.IsType<ObjectResult>(await rig.Controller.Connect(new IfoodStoreConnectionRequest { MerchantId = Merchant.ToString() }, default)).StatusCode);
    }
}

public sealed class IfoodConfigAuthorizationTests
{
    private static readonly Guid Store = Guid.NewGuid(), Merchant = Guid.NewGuid();

    private static (IfoodConfigController Controller, Mock<IIfoodRuntimeSettingsRepository> Repo, Mock<IIfoodConnectionHealth> Health) Rig(IfoodMerchantCheck check)
    {
        var repo = new Mock<IIfoodRuntimeSettingsRepository>();
        var probe = new Mock<IIfoodMerchantProbe>();
        probe.Setup(p => p.CheckAsync(Merchant, It.IsAny<CancellationToken>())).ReturnsAsync(check);
        var health = new Mock<IIfoodConnectionHealth>();
        var context = new DefaultHttpContext(); context.Items["IsSuperAdmin"] = true;
        var controller = new IfoodConfigController(repo.Object, new IfoodSecretProtector(Microsoft.Extensions.Options.Options.Create(new IfoodOptions())),
            probe.Object, health.Object, new Mock<IIfoodConnectionLifecycle>().Object)
        { ControllerContext = new ControllerContext { HttpContext = context } };
        return (controller, repo, health);
    }

    [Fact]
    public async Task AuthorizationRequiresIfoodToConfirmTheMerchantNow()
    {
        var (controller, repo, _) = Rig(new IfoodMerchantCheck(false, "NOT_AUTHORIZED", "ainda nao"));

        var result = Assert.IsType<ObjectResult>(await controller.Authorize(new IfoodConnectionRequest { MerchantId = Merchant, EstabelecimentoId = Store }, default));

        Assert.Equal(422, result.StatusCode);
        Assert.Equal("IFOOD_MERCHANT_NOT_AUTHORIZED_AT_IFOOD", Assert.IsType<ApiResponse<object>>(result.Value).Code);
        repo.Verify(r => r.AuthorizeConnectionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task IfoodOutageIsNotMistakenForRefusal()
    {
        var (controller, repo, _) = Rig(new IfoodMerchantCheck(false, "UNAVAILABLE", "fora"));
        Assert.Equal(503, Assert.IsType<ObjectResult>(await controller.Authorize(new IfoodConnectionRequest { MerchantId = Merchant, EstabelecimentoId = Store }, default)).StatusCode);
        repo.Verify(r => r.AuthorizeConnectionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task ConfirmedMerchantIsAuthorizedAndTheCheckIsRecorded()
    {
        var (controller, repo, health) = Rig(new IfoodMerchantCheck(true, "AUTHORIZED", "ok"));

        Assert.IsType<OkObjectResult>(await controller.Authorize(new IfoodConnectionRequest { MerchantId = Merchant, EstabelecimentoId = Store }, default));

        repo.Verify(r => r.AuthorizeConnectionAsync(Merchant, Store, It.IsAny<CancellationToken>()), Times.Once());
        health.Verify(h => h.RecordRemoteCheckAsync(Merchant, "AUTHORIZED", It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task RecheckSuccessReleasesTheMerchantForTheNextCycle()
    {
        var (controller, _, health) = Rig(new IfoodMerchantCheck(true, "AUTHORIZED", "ok"));
        Assert.IsType<OkObjectResult>(await controller.Recheck(Merchant, default));
        health.Verify(h => h.RetryNowAsync(Merchant, It.IsAny<CancellationToken>()), Times.Once());

        var (failing, _, failingHealth) = Rig(new IfoodMerchantCheck(false, "NOT_AUTHORIZED", "x"));
        Assert.IsType<OkObjectResult>(await failing.Recheck(Merchant, default));
        failingHealth.Verify(h => h.RetryNowAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
        failingHealth.Verify(h => h.RecordRemoteCheckAsync(Merchant, "NOT_AUTHORIZED", It.IsAny<CancellationToken>()), Times.Once());
    }

    [Theory]
    [InlineData("IFOOD_CONNECTION_HAS_PENDING_WORK", 409)]
    [InlineData("IFOOD_CONNECTION_HAS_HISTORY", 409)]
    [InlineData("IFOOD_CONNECTION_MERCHANT_LINKED_ELSEWHERE", 409)]
    [InlineData("IFOOD_CONNECTION_STORE_HAS_ACTIVE", 409)]
    [InlineData("IFOOD_CONNECTION_NOT_FOUND", 404)]
    [InlineData("IFOOD_CONNECTION_INVALID_INPUT", 400)]
    public void LifecycleErrorsMapToStableStatuses(string code, int status)
        => Assert.Equal(status, Assert.IsType<ObjectResult>(IfoodConfigController.LifecycleError(new IfoodIntegrationException(code), "x")).StatusCode);

    [Fact]
    public async Task NonSuperadminCannotTransferOrRemove()
    {
        var (controller, _, _) = Rig(new IfoodMerchantCheck(true, "AUTHORIZED", "ok"));
        controller.ControllerContext.HttpContext.Items["IsSuperAdmin"] = false;
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.Transfer(Merchant, new IfoodConnectionTransferRequest { EstabelecimentoId = Store }, default)).StatusCode);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.Remove(Merchant, Store, default)).StatusCode);
    }
}

public sealed class IfoodLifecycleMigrationContractTests
{
    private static string Sql() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261027_01_ifood_connection_lifecycle.sql"));

    [Fact]
    public void MigrationOnlyAddsStructureAndNeverDeletesOrMovesData()
    {
        var sql = Sql();
        Assert.DoesNotContain("DELETE FROM", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUNCATE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"UPDATE\s+ifood_(order|events)", sql);
        Assert.Contains("ADD COLUMN IF NOT EXISTS poll_blocked_until_utc", sql);
        Assert.Contains("ux_ifood_connections_active_merchant", sql);
        Assert.Contains("PRIMARY KEY (merchant_id, estabelecimento_id)", sql);
        Assert.Contains("ifood_connection_audit", sql);
    }

    [Fact]
    public void AuditTableHasNoForeignKeySoHistorySurvivesRemoval()
    {
        var sql = Sql();
        var audit = sql[sql.IndexOf("CREATE TABLE IF NOT EXISTS ifood_connection_audit", StringComparison.Ordinal)..];
        audit = audit[..audit.IndexOf(");", StringComparison.Ordinal)];
        Assert.DoesNotContain("REFERENCES", audit);
    }
}
