using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Auth;
using APIBack.Model.Auth;

namespace APIBack.Service.Interface
{
    public interface IAuthService
    {
        Task<TokenResponse> LoginAsync(LoginRequest request);
        Task<TokenResponse> RefreshTokenAsync(RefreshTokenRequest request, string? ipAddress, string? userAgent);
        Task LogoutAsync(int userId, LogoutRequest request, string? ipAddress, string? userAgent);
        Task<OAuthAuthorizationResponse> IniciarLoginGoogleAsync(string? redirectUri);
        Task<OAuthCallbackResult> ProcessarCallbackGoogleAsync(string code, string state);
        Task<string?> ConsumirRedirectGoogleAsync(string state);
    }
}
