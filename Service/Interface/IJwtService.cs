using System.Security.Claims;
using APIBack.Model.Auth;

namespace APIBack.Service.Interface
{
    public interface IJwtService
    {
        string GenerateToken(JwtPayload payload);
        JwtPayload ValidateToken(string token);
        string GenerateRefreshToken();
        ClaimsPrincipal GetPrincipalFromExpiredToken(string token);
    }
}

