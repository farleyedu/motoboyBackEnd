using Microsoft.Extensions.Options;

namespace APIBack.Integrations.Ifood;

public interface IIfoodTokenProvider
{
    Task<IfoodAccessToken> GetAsync(CancellationToken cancellationToken = default);
    void Invalidate(IfoodAccessToken rejectedToken);
}

// Uma reserva por processo; não persiste access token nem faz renovação em loop.
public sealed class IfoodTokenProvider : IIfoodTokenProvider
{
    private readonly IIfoodAuthenticationClient _authentication;
    private readonly IfoodOptions _options;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IfoodAccessToken? _cached;
    private IfoodIntegrationException? _failure;
    private DateTimeOffset _retryAt;
    private int _consecutiveFailures;

    public IfoodTokenProvider(IIfoodAuthenticationClient authentication, IOptions<IfoodOptions> options, TimeProvider clock)
        => (_authentication, _options, _clock) = (authentication, options.Value, clock);

    public async Task<IfoodAccessToken> GetAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) throw new IfoodIntegrationException("IFOOD_DISABLED");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = Volatile.Read(ref _cached);
            var now = _clock.GetUtcNow();
            if (current != null && current.ExpiresAtUtc - now >
                TimeSpan.FromSeconds(Math.Min(_options.RenewalSkewSeconds, current.Lifetime.TotalSeconds / 10)))
                return current;
            if (_failure != null && now < _retryAt)
                throw new IfoodIntegrationException(_failure.Code, _failure.RemoteStatus,
                    _failure.Retryable, _retryAt - now);
            try
            {
                var fresh = await _authentication.RequestTokenAsync(cancellationToken);
                Volatile.Write(ref _cached, fresh);
                _failure = null;
                _consecutiveFailures = 0;
                return fresh;
            }
            catch (IfoodIntegrationException ex)
            {
                // Não devolve token antigo como fallback de credencial revogada/falha de renovação.
                Volatile.Write(ref _cached, null);
                _failure = ex;
                _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 6);
                var delay = TimeSpan.FromSeconds(Math.Min(300,
                    _options.FailureCooldownSeconds * Math.Pow(2, _consecutiveFailures - 1)));
                if (ex.RetryAfter is { } retry && retry > delay) delay = retry;
                var failureAt = _clock.GetUtcNow();
                _retryAt = delay > DateTimeOffset.MaxValue - failureAt ? DateTimeOffset.MaxValue : failureAt.Add(delay);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    // Um 401 tardio do token anterior não pode apagar a renovação já concluída.
    public void Invalidate(IfoodAccessToken rejectedToken)
        => Interlocked.CompareExchange(ref _cached, null, rejectedToken);
}
