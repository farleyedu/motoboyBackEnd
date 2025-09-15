// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using Microsoft.Extensions.Configuration;
using APIBack.Automation.Interfaces;

namespace APIBack.Automation.Services
{
    // Armazena um Access Token do WhatsApp em memória, com fallback para configuração.
    public class InMemoryWhatsAppTokenProvider : IWhatsAppTokenProvider
    {
        private readonly IConfiguration _configuration;
        private string? _current;
        public DateTimeOffset? LastUpdatedUtc { get; private set; }

        public InMemoryWhatsAppTokenProvider(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public string? GetAccessToken()
        {
            if (!string.IsNullOrWhiteSpace(_current))
                return _current;

            // Fallback: lê de appsettings se ainda não foi definido em memória
            return _configuration["WhatsApp:AccessToken"]
                ?? _configuration["Automation:Meta:AccessToken"]; // fallback alternativo
        }

        public void SetAccessToken(string token)
        {
            _current = token;
            LastUpdatedUtc = DateTimeOffset.UtcNow;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

