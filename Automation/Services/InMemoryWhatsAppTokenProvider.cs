// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using Microsoft.Extensions.Configuration;
using APIBack.Automation.Interfaces;

namespace APIBack.Automation.Services
{
    // Reads the WhatsApp Access Token directly from configuration and allows runtime override.
    public class InMemoryWhatsAppTokenProvider : IWhatsAppTokenProvider
    {
        private readonly IConfiguration _configuration;
        private string? _tokenEmMemoria;
        private DateTimeOffset? _lastUpdatedUtc;
        private readonly object _sync = new();

        public InMemoryWhatsAppTokenProvider(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public string? GetAccessToken()
        {
            lock (_sync)
            {
                return _tokenEmMemoria
                    ?? _configuration["WhatsApp:AccessToken"]
                    ?? _configuration["Automation:Meta:AccessToken"];
            }
        }

        public void SetAccessToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new ArgumentException("Token inválido", nameof(token));
            }

            lock (_sync)
            {
                _tokenEmMemoria = token;
                _lastUpdatedUtc = DateTimeOffset.UtcNow;
            }
        }

        public DateTimeOffset? LastUpdatedUtc
        {
            get
            {
                lock (_sync)
                {
                    return _lastUpdatedUtc;
                }
            }
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
