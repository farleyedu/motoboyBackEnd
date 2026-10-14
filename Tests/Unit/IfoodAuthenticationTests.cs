using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using APIBack.Integrations.Ifood;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class IfoodAuthenticationTests
{
    internal static IfoodOptions EnabledOptions() => new()
    {
        Enabled = true, ClientId = "test-client", ClientSecret = "secret-test-only-&=+",
        ActiveEncryptionKeyId = "key1", EncryptionKeys = new() { ["key1"] = Convert.ToBase64String(new byte[32]) }
    };
    // IOptionsMonitor<T> fixo para testes: nenhum worker/cliente precisa de hot-reload real aqui,
    // só do mesmo contrato que o DI de produção usa (ver IF-13).
    internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string> listener) => NoopDisposable.Instance;
        private sealed class NoopDisposable : IDisposable { public static readonly NoopDisposable Instance = new(); public void Dispose() { } }
    }
    internal static IOptionsMonitor<IfoodOptions> Monitor(IfoodOptions? options = null) => new StaticOptionsMonitor<IfoodOptions>(options ?? EnabledOptions());
    internal sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
    internal sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Send { get; set; }
            = (_, _) => Task.FromResult(TokenResponse());
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return Send(request, cancellationToken); }
    }
    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { MaxResponseContentBufferSize = 32768 };
    }
    internal static HttpResponseMessage TokenResponse(string token = "test-token", int seconds = 600)
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { accessToken = token, type = "bearer", expiresIn = seconds })) };
    private static IfoodAuthenticationClient Client(Handler handler, Clock? clock = null, IfoodOptions? options = null)
        => new(new Factory(handler), Monitor(options), new IfoodSecretProtector(Microsoft.Extensions.Options.Options.Create(options ?? EnabledOptions())), clock ?? new Clock());
    private static IfoodTokenProvider Provider(Handler handler, Clock clock, IfoodOptions? options = null)
        => new(Client(handler, clock, options), Monitor(options), clock);

    [Fact]
    public async Task RequestsOnlyOfficialEndpointAndEncodedCentralizedCredentials()
    {
        var clock = new Clock();
        var handler = new Handler();
        handler.Send = async (request, _) =>
        {
            Assert.Equal(IfoodAuthenticationClient.TokenEndpoint, request.RequestUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            var body = await request.Content.ReadAsStringAsync();
            Assert.Contains("grantType=client_credentials", body);
            Assert.Contains("clientId=test-client", body);
            Assert.Contains("clientSecret=secret-test-only-%26%3D%2B", body);
            Assert.DoesNotContain("refreshToken", body);
            clock.Advance(2);
            return TokenResponse(seconds: 120);
        };
        var token = await Client(handler, clock).RequestTokenAsync();
        Assert.Equal(clock.GetUtcNow().AddSeconds(118), token.ExpiresAtUtc);
        Assert.DoesNotContain(token.Value, token.ToString());
        Assert.DoesNotContain(token.Value, JsonSerializer.Serialize(token));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("invalid secret body")]
    [InlineData("{\"accessToken\":\"ok\",\"type\":\"bearer\",\"expiresIn\":0}")]
    [InlineData("{\"accessToken\":\"ok\",\"type\":\"bearer\",\"expiresIn\":-1}")]
    [InlineData("{\"accessToken\":\"ok\",\"type\":\"bearer\",\"expiresIn\":\"600\"}")]
    [InlineData("{\"accessToken\":\"ok\",\"type\":\"other\",\"expiresIn\":600}")]
    [InlineData("{\"accessToken\":\"secret token\\r\\nheader\",\"type\":\"bearer\",\"expiresIn\":600}")]
    public async Task RejectsInvalidSuccessWithoutExposingItsBody(string body)
    {
        var handler = new Handler { Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }) };
        var ex = await Assert.ThrowsAsync<IfoodIntegrationException>(() => Client(handler).RequestTokenAsync());
        Assert.Equal("IFOOD_AUTH_INVALID_RESPONSE", ex.Code);
        Assert.Null(ex.InnerException);
        Assert.DoesNotContain(body, ex.ToString());
    }

    [Theory]
    [InlineData(401, "IFOOD_AUTH_BAD_CREDENTIALS", false)]
    [InlineData(403, "IFOOD_AUTH_FORBIDDEN", false)]
    [InlineData(429, "IFOOD_AUTH_RATE_LIMIT", true)]
    [InlineData(500, "IFOOD_AUTH_REMOTE_ERROR", true)]
    [InlineData(302, "IFOOD_AUTH_REMOTE_ERROR", false)]
    public async Task ClassifiesHttpErrorsWithoutReturningRemoteSecrets(int status, string code, bool retryable)
    {
        var handler = new Handler { Send = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("secret-test-only-&=+") }) };
        var ex = await Assert.ThrowsAsync<IfoodIntegrationException>(() => Client(handler).RequestTokenAsync());
        Assert.Equal(code, ex.Code);
        Assert.Equal(retryable, ex.Retryable);
        Assert.Equal((HttpStatusCode)status, ex.RemoteStatus);
        Assert.DoesNotContain("secret-test-only", ex.ToString());
    }

    [Fact]
    public async Task ReusesOneTokenForConcurrentCallsAndRenewsUsingResponseExpiration()
    {
        var clock = new Clock();
        var handler = new Handler { Send = async (_, _) => { await Task.Yield(); return TokenResponse(seconds: 120); } };
        var provider = Provider(handler, clock);
        var tokens = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => provider.GetAsync()));
        Assert.Equal(1, handler.Calls);
        Assert.All(tokens, token => Assert.Same(tokens[0], token));
        clock.Advance(107);
        Assert.Same(tokens[0], await provider.GetAsync());
        clock.Advance(2);
        var fresh = await provider.GetAsync();
        Assert.NotSame(tokens[0], fresh);
        Assert.Equal(2, handler.Calls);
        provider.Invalidate(tokens[0]); // 401 antigo chega depois da renovação.
        Assert.Same(fresh, await provider.GetAsync());
        provider.Invalidate(fresh);
        Assert.NotSame(fresh, await provider.GetAsync());
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RateLimitHonorsDeltaOrDateAndPreventsAuthenticationStorm(bool dateHeader)
    {
        var clock = new Clock();
        var handler = new Handler();
        handler.Send = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = dateHeader ? new RetryConditionHeaderValue(clock.GetUtcNow().AddSeconds(7200)) : new RetryConditionHeaderValue(TimeSpan.FromSeconds(7200));
            return Task.FromResult(response);
        };
        var provider = Provider(handler, clock);
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => provider.GetAsync());
        clock.Advance(7199);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ => await Assert.ThrowsAsync<IfoodIntegrationException>(() => provider.GetAsync())));
        Assert.Equal(1, handler.Calls);
        clock.Advance(1);
        handler.Send = (_, _) => Task.FromResult(TokenResponse());
        Assert.Equal("test-token", (await provider.GetAsync()).Value);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task FailureBackoffGrowsAndDoesNotReturnOldTokenAfterRenewalFails()
    {
        var clock = new Clock();
        var handler = new Handler();
        var provider = Provider(handler, clock);
        await provider.GetAsync();
        clock.Advance(541);
        handler.Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => provider.GetAsync());
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => provider.GetAsync());
        Assert.Equal(2, handler.Calls);
        clock.Advance(10);
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => provider.GetAsync());
        clock.Advance(19);
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => provider.GetAsync());
        Assert.Equal(3, handler.Calls);
        clock.Advance(1);
        handler.Send = (_, _) => Task.FromResult(TokenResponse());
        await provider.GetAsync();
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task TransportAndTimeoutExceptionsAreSanitizedAndCallerCancellationCanRetry()
    {
        var handler = new Handler { Send = (_, _) => throw new HttpRequestException("secret-test-only") };
        var ex = await Assert.ThrowsAsync<IfoodIntegrationException>(() => Client(handler).RequestTokenAsync());
        Assert.Equal("IFOOD_AUTH_TRANSPORT", ex.Code);
        Assert.DoesNotContain("secret-test-only", ex.ToString());
        handler.Send = (_, _) => throw new TaskCanceledException("secret-test-only");
        ex = await Assert.ThrowsAsync<IfoodIntegrationException>(() => Client(handler).RequestTokenAsync());
        Assert.Equal("IFOOD_AUTH_TIMEOUT", ex.Code);
        using var cancelled = new CancellationTokenSource();
        handler.Send = (_, _) => { cancelled.Cancel(); throw new TaskCanceledException("secret-test-only"); };
        var provider = Provider(handler, new Clock());
        var cancellation = await Assert.ThrowsAsync<OperationCanceledException>(() => provider.GetAsync(cancelled.Token));
        Assert.DoesNotContain("secret-test-only", cancellation.ToString());
        handler.Send = (_, _) => Task.FromResult(TokenResponse());
        await provider.GetAsync(); // Cancelamento não cria cooldown.
    }

    [Fact]
    public async Task BoundsTokenAndResponseSizeAndRejectsExpiredResponses()
    {
        var clock = new Clock();
        var handler = new Handler { Send = (_, _) => Task.FromResult(TokenResponse(new string('a', 8001))) };
        Assert.Equal("IFOOD_AUTH_INVALID_RESPONSE", (await Assert.ThrowsAsync<IfoodIntegrationException>(() => Client(handler).RequestTokenAsync())).Code);
        handler.Send = (_, _) => Task.FromResult(TokenResponse(new string('a', 40000)));
        Assert.Equal("IFOOD_AUTH_TRANSPORT", (await Assert.ThrowsAsync<IfoodIntegrationException>(() => Client(handler).RequestTokenAsync())).Code);
        handler.Send = (_, _) => { clock.Advance(10); return Task.FromResult(TokenResponse(seconds: 5)); };
        Assert.Equal("IFOOD_AUTH_EXPIRED_RESPONSE", (await Assert.ThrowsAsync<IfoodIntegrationException>(() => Client(handler, clock).RequestTokenAsync())).Code);
    }

    [Fact]
    public async Task DisabledModuleWorksWithoutCredentialsAndMakesNoHttpCalls()
    {
        var handler = new Handler();
        var provider = Provider(handler, new Clock(), new IfoodOptions());
        Assert.Equal("IFOOD_DISABLED", (await Assert.ThrowsAsync<IfoodIntegrationException>(() => provider.GetAsync())).Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task DependencyRegistrationIsIdleAndHttpLogsCannotExposeTransportSecrets()
    {
        var options = EnabledOptions();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ifood:Enabled"] = "true", ["Ifood:ClientId"] = options.ClientId, ["Ifood:ClientSecret"] = options.ClientSecret,
            ["Ifood:ActiveEncryptionKeyId"] = "key1", ["Ifood:EncryptionKeys:key1"] = options.EncryptionKeys["key1"]
        }).Build();
        var logs = new List<string>();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(new CaptureLogs(logs)));
        services.AddIfoodIntegration(configuration);
        var handler = new Handler { Send = (_, _) => throw new HttpRequestException(options.ClientSecret) };
        services.AddHttpClient(IfoodAuthenticationClient.ClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        using var container = services.BuildServiceProvider();
        var provider = container.GetRequiredService<IIfoodTokenProvider>();
        Assert.Equal(0, handler.Calls);
        var exception = await Assert.ThrowsAsync<IfoodIntegrationException>(() => provider.GetAsync());
        Assert.DoesNotContain(options.ClientSecret, exception.ToString());
        Assert.DoesNotContain(options.ClientSecret, string.Join("\n", logs));
        Assert.Equal(1, handler.Calls);
    }

    private sealed class CaptureLogs(List<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(messages);
        public void Dispose() { }
        private sealed class Logger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            { lock (messages) messages.Add(formatter(state, ex) + ex); }
        }
    }
}
