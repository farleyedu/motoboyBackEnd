using System;

namespace APIBack.DTOs.Auth
{
    public class OAuthAuthorizationResponse
    {
        public string AuthorizationUrl { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
