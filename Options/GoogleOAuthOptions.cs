using System.Collections.Generic;

namespace APIBack.Options
{
    public class GoogleOAuthOptions
    {
        public bool Enabled { get; set; } = true;
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public string RedirectUri { get; set; } = string.Empty;
        public List<string> Scopes { get; set; } = new() { "openid", "profile", "email" };
        public string AccessType { get; set; } = "offline";
        public string Prompt { get; set; } = "select_account";
        public int StateTtlMinutes { get; set; } = 5;
        public List<string> AllowedPostLoginRedirects { get; set; } = new();
        public string? DefaultPostLoginRedirect { get; set; }
    }
}
