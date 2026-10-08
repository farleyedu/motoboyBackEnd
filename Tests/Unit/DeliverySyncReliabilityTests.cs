using System.Text.Json;
using APIBack.DTOs.Tracking;
using APIBack.Middleware;
using APIBack.Model.Auth;
using APIBack.Model.Tracking;
using APIBack.Options;
using APIBack.Repository.Interface;
using APIBack.Service;
using APIBack.Service.Interface;
using APIBack.Services;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Unit;

public class DeliverySyncReliabilityTests
{
    [Fact]
    public async Task DatabaseUnavailableDoesNotBecomeAuthenticationFailure()
    {
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.ValidateToken("a.b.c")).Returns(new JwtPayload {
            TokenUse = "delivery_operational", MotoboySessionId = Guid.NewGuid(), MotoboyId = 1, SessionEpoch = 1 });
        await using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=delivery_test;Username=delivery_test;Timeout=1;Pooling=false");
        using var services = new ServiceCollection().AddSingleton(source).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Headers.Authorization = "Bearer a.b.c";
        context.Response.Body = new MemoryStream();
        var called = false;
        var auth = new JwtAuthenticationMiddleware(_ => { called = true; return Task.CompletedTask; });
        var errors = new ExceptionHandlingMiddleware(c => auth.InvokeAsync(c, jwt.Object, new ConfigurationBuilder().Build()),
            NullLogger<ExceptionHandlingMiddleware>.Instance);
        await errors.InvokeAsync(context);
        Assert.False(called);
        Assert.Equal(503, context.Response.StatusCode);
        Assert.Equal("3", context.Response.Headers.RetryAfter.ToString());
        context.Response.Body.Position = 0;
        var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("DATABASE_UNAVAILABLE", json.RootElement.GetProperty("code").GetString());
        Assert.False(context.Items.ContainsKey("AuthenticationFailureCode"));
        Assert.False(context.Items.ContainsKey("JwtPayload"));
    }

    [Fact]
    public async Task RequestMetricsObserveFinal503AndDoNotIncludeCredentials()
    {
        using var services = new ServiceCollection().AddMetrics().AddSingleton<DeliverySyncMetrics>().BuildServiceProvider();
        var metrics = services.GetRequiredService<DeliverySyncMetrics>();
        var observed = new List<KeyValuePair<string, object?>[]>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, collector) => {
            if (instrument.Meter.Name == DeliverySyncMetrics.MeterName) collector.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) => observed.Add(tags.ToArray())); listener.Start();
        var context = new DefaultHttpContext(); context.Request.Path = "/api/v2/motoboys/me/session/queue";
        context.Request.Headers.Authorization = "Bearer secret-test"; context.Response.Body = new MemoryStream();
        var errors = new ExceptionHandlingMiddleware(_ => throw new TimeoutException(), NullLogger<ExceptionHandlingMiddleware>.Instance);
        var middleware = new DeliverySyncMetricsMiddleware(c => errors.InvokeAsync(c), NullLogger<DeliverySyncMetricsMiddleware>.Instance);
        await middleware.InvokeAsync(context, metrics);
        var tags = Assert.Single(observed);
        Assert.Contains(tags, t => t.Key == "status" && Equals(t.Value, 503));
        Assert.Contains(tags, t => t.Key == "operation" && Equals(t.Value, "queue"));
        Assert.DoesNotContain(tags, t => t.Value?.ToString()?.Contains("secret-test") == true);
    }

    [Fact]
    public async Task InvalidJwtStillReachesAuthorizationWithoutPayload()
    {
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.ValidateToken("a.b.c")).Throws(new ArgumentException("invalid"));
        var context = new DefaultHttpContext(); context.Request.Headers.Authorization = "Bearer a.b.c";
        var called = false;
        await new JwtAuthenticationMiddleware(_ => { called = true; return Task.CompletedTask; })
            .InvokeAsync(context, jwt.Object, new ConfigurationBuilder().Build());
        Assert.True(called); Assert.False(context.Items.ContainsKey("JwtPayload"));
    }

    [Fact]
    public void BatchOnlyLatestNewSampleUpdatesCurrent()
    {
        var locations = Enumerable.Range(1, 20).Select(Write).ToArray();
        var result = OperationalLocationBatchRules.Plan(locations, Array.Empty<StoredLocationSample>(), 0, 4, DateTimeOffset.UtcNow);
        Assert.Equal(20, result.Count); Assert.All(result, r => Assert.Equal("accepted", r.Outcome));
        Assert.Single(result.Where(r => r.UpdatedCurrent)); Assert.True(result[^1].UpdatedCurrent);
    }

    [Fact]
    public void BatchAcknowledgesExactReplayAndStaleWithoutMovingCurrent()
    {
        var replay = Write(10); var old = Write(5);
        var stored = new StoredLocationSample { SampleId = replay.SampleId, Sequence = replay.Sequence,
            PayloadHash = replay.PayloadHash, UpdatedCurrent = true, ReceivedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) };
        var result = OperationalLocationBatchRules.Plan(new[] { old, replay }, new[] { stored }, 10, 8, DateTimeOffset.UtcNow);
        Assert.Equal("stale", result[0].Outcome); Assert.Equal("duplicate", result[1].Outcome);
        Assert.Equal(stored.ReceivedAtUtc, result[1].ReceivedAtUtc);
        Assert.Equal("STALE_SEQUENCE", Assert.Throws<DeliveryDomainException>(() =>
            OperationalLocationBatchRules.Plan(new[] { old }, Array.Empty<StoredLocationSample>(), 10, 8, DateTimeOffset.UtcNow, true)).Code);
    }

    [Fact]
    public void ReusingIdentityForOtherContentIsConflict()
    {
        var sample = Write(1);
        var known = new StoredLocationSample { SampleId = sample.SampleId, Sequence = 1, PayloadHash = "other" };
        Assert.Equal("SEQUENCE_CONFLICT", Assert.Throws<DeliveryDomainException>(() =>
            OperationalLocationBatchRules.Plan(new[] { sample }, new[] { known }, 1, 2, DateTimeOffset.UtcNow)).Code);
    }

    [Fact]
    public void BatchShapeRejectsUnboundedUnorderedAndRepeatedIds()
    {
        var a = Request(1); var b = Request(2);
        Assert.Throws<DeliveryDomainException>(() => OperationalLocationBatchRules.ValidateShape(new() { Samples = Array.Empty<OperationalLocationRequest>() }));
        Assert.Throws<DeliveryDomainException>(() => OperationalLocationBatchRules.ValidateShape(new() { Samples = Enumerable.Range(1, 21).Select(Request).ToArray() }));
        Assert.Throws<DeliveryDomainException>(() => OperationalLocationBatchRules.ValidateShape(new() { Samples = new[] { b, a } }));
        b.SampleId = a.SampleId;
        Assert.Throws<DeliveryDomainException>(() => OperationalLocationBatchRules.ValidateShape(new() { Samples = new[] { a, b } }));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-300, 0)]
    public async Task InvalidPointDoesNotBlockValidPointAndOldPointDoesNotFinishReturn(int seconds, int expectedReturnCalls)
    {
        var repo = new Mock<IOperationalSessionRepository>(); var queue = new Mock<IPedidoQueueRepository>();
        var payload = new JwtPayload { TokenUse = "delivery_operational", MotoboySessionId = Guid.NewGuid(), MotoboyId = 2,
            SessionEpoch = 5, EstabelecimentoId = Guid.NewGuid() };
        repo.Setup(r => r.WriteLocationsAsync(payload.MotoboySessionId.Value, 2, 5,
            It.IsAny<IReadOnlyList<OperationalLocationWrite>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new OperationalLocationWriteResult { UpdatedCurrent = true, SessionVersion = 2 } });
        var service = new OperationalSessionService(repo.Object, new Mock<IJwtService>().Object,
            Microsoft.Extensions.Options.Options.Create(new DeliveryTrackingOptions { Enabled = true }), queue.Object);
        var invalid = Request(1); invalid.Latitude = 500;
        var valid = Request(2); valid.CapturedAtUtc = DateTimeOffset.UtcNow.AddSeconds(seconds);
        var result = await service.ReceiveLocationsAsync(payload, new() { Samples = new[] { invalid, valid } });
        Assert.Equal("rejected", result.Samples[0].Outcome); Assert.Equal("LOCATION_INVALID", result.Samples[0].Code);
        Assert.Equal("accepted", result.Samples[1].Outcome);
        queue.Verify(q => q.TryFinishReturnByLocationAsync(payload.EstabelecimentoId.Value, 2, valid.Latitude!.Value, valid.Longitude!.Value),
            Times.Exactly(expectedReturnCalls));
    }

    [Fact]
    public async Task CancellationFlowsToHeartbeatRepository()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var repo = new Mock<IOperationalSessionRepository>();
        repo.Setup(r => r.HeartbeatAsync(It.IsAny<Guid>(), 2, 5, cancellation.Token)).ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var service = new OperationalSessionService(repo.Object, new Mock<IJwtService>().Object,
            Microsoft.Extensions.Options.Options.Create(new DeliveryTrackingOptions { Enabled = true }), new Mock<IPedidoQueueRepository>().Object);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.HeartbeatAsync(new JwtPayload {
            TokenUse = "delivery_operational", MotoboySessionId = Guid.NewGuid(), MotoboyId = 2, SessionEpoch = 5 }, cancellation.Token));
    }

    private static OperationalLocationRequest Request(int sequence) => new() { SampleId = Guid.NewGuid(), Sequence = sequence,
        CapturedAtUtc = DateTimeOffset.UtcNow, Latitude = -23.5, Longitude = -46.6, AccuracyMeters = 10 };
    private static OperationalLocationWrite Write(int sequence) => DeliveryTrackingPolicy.ValidateAndMap(Request(sequence), new(), DateTimeOffset.UtcNow);
}
