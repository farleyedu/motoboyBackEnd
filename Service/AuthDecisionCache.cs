using APIBack.Options;
using APIBack.Service.Interface;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace APIBack.Service;

public sealed class AuthDecisionCache(IMemoryCache cache, IOptions<AuthCacheOptions> options,
    TimeProvider? clock = null) : IAuthDecisionCache
{
    private readonly object _gate = new();
    private readonly Dictionary<AuthDecisionKey, Entry> _entries = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _generation;
    private sealed record Entry(DateTimeOffset Until);
    private sealed record MemoryKey(AuthDecisionCache Owner, AuthDecisionKey Decision);

    public async Task<bool> IsAllowedAsync(AuthDecisionKey key, Func<Task<bool>> evaluate)
    {
        var ttl = Math.Clamp(options.Value.TtlSeconds, 0, 300);
        if (ttl == 0) return await evaluate();
        while (true)
        {
            long generation;
            var memoryKey = new MemoryKey(this, key);
            lock (_gate)
            {
                if (cache.TryGetValue<Entry>(memoryKey, out var hit) && hit!.Until > _clock.GetUtcNow()) return true;
                generation = _generation;
            }
            if (!await evaluate()) return false;
            lock (_gate)
            {
                // Uma consulta iniciada antes da revogação não pode recolocar o acesso no cache.
                if (generation != _generation) continue;
                var entry = new Entry(_clock.GetUtcNow().AddSeconds(ttl));
                _entries[key] = entry;
                cache.Set(memoryKey, entry, new MemoryCacheEntryOptions {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ttl)
                }.RegisterPostEvictionCallback((_, value, _, _) => {
                    lock (_gate)
                        if (_entries.TryGetValue(key, out var current) && ReferenceEquals(value, current)) _entries.Remove(key);
                }));
                return true;
            }
        }
    }

    private void Invalidate(Func<AuthDecisionKey, bool> matches)
    {
        lock (_gate)
        {
            _generation++;
            foreach (var key in _entries.Keys.Where(matches).ToArray())
            {
                _entries.Remove(key);
                cache.Remove(new MemoryKey(this, key));
            }
        }
    }
    public void InvalidateUser(int id) => Invalidate(k => k.UserId == id);
    public void InvalidateSession(Guid id) => Invalidate(k => k.MotoboySessionId == id || k.MotoboyLoginSessionId == id);
    public void InvalidateMotoboy(int id) => Invalidate(k => k.MotoboyId == id);
    public void InvalidateEstablishment(Guid id) => Invalidate(k => k.EstabelecimentoId == id);
    // JWTs operacionais legados carregam a loja, mas não o id da empresa.
    public void InvalidateCompany(Guid id) => Invalidate(k => k.EmpresaId == id ||
        (!k.EmpresaId.HasValue && k.EstabelecimentoId.HasValue));
}
