using System;
using APIBack.Automation.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace APIBack.Automation.Services
{
    public class WebhookMessageCache : IWebhookMessageCache
    {
        private readonly IMemoryCache _cache;
        private static readonly MemoryCacheEntryOptions EntryOptions = new()
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15),
            Priority = CacheItemPriority.Low
        };

        public WebhookMessageCache(IMemoryCache cache)
        {
            _cache = cache;
        }

        public bool TryRegister(string? messageId)
        {
            if (string.IsNullOrWhiteSpace(messageId))
            {
                // Se não há ID, não conseguimos deduplicar – processa normalmente.
                return true;
            }

            if (_cache.TryGetValue(messageId, out _))
            {
                return false;
            }

            _cache.Set(messageId, true, EntryOptions);
            return true;
        }
    }
}
