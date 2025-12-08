using APIBack.Model.Auth;

namespace APIBack.DTOs.Auth
{
    public class OAuthCallbackResult
    {
        public TokenResponse Token { get; set; } = null!;
        public string? RedirectUri { get; set; }
    }
}
