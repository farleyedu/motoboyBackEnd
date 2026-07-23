using System.Security.Claims;
using APIBack.Model.Auth;

namespace APIBack.Service.Interface
{
    public interface IJwtService
    {
        string GenerateToken(JwtPayload payload);
        string GenerateToken(JwtPayload payload, TimeSpan lifetime);
        JwtPayload ValidateToken(string token);
        string GenerateRefreshToken();
        ClaimsPrincipal GetPrincipalFromExpiredToken(string token);
    }
}

