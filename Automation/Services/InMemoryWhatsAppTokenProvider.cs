// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using Microsoft.Extensions.Configuration;
using APIBack.Automation.Interfaces;

namespace APIBack.Automation.Services
{
    // Reads the WhatsApp Access Token directly from configuration.
    public class InMemoryWhatsAppTokenProvider : IWhatsAppTokenProvider
    {
        private readonly IConfiguration _configuration;

        public InMemoryWhatsAppTokenProvider(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public string? GetAccessToken()
        {
            return _configuration["WhatsApp:AccessToken"]
                ?? _configuration["Automation:Meta:AccessToken"]; // alternate fallback
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

