using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using APIBack.Model.Auth;
using APIBack.Service.Interface;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using JwtSecurityToken = System.IdentityModel.Tokens.Jwt.JwtSecurityToken;
using JwtSecurityTokenHandler = System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler;
using JwtRegisteredClaimNames = System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames;

namespace APIBack.Service
{
    public class JwtService : IJwtService
    {
        private readonly string _secretKey;
        private readonly string _issuer;
        private readonly string _audience;
        private readonly int _expirationMinutes;
        private readonly int _refreshTokenExpirationDays;

        public JwtService(IConfiguration configuration)
        {
            var jwtSection = configuration.GetSection("Jwt");

            _secretKey = jwtSection["SecretKey"]
                         ?? throw new InvalidOperationException("Jwt:SecretKey n\u00e3o configurado.");
            _issuer = jwtSection["Issuer"] ?? "ZippyGo";
            _audience = jwtSection["Audience"] ?? "ZippyGoAPI";
            _expirationMinutes = int.TryParse(jwtSection["ExpirationMinutes"], out var minutes)
                ? minutes
                : 60;
            _refreshTokenExpirationDays = int.TryParse(jwtSection["RefreshTokenExpirationDays"], out var days)
                ? days
                : 7;
        }

        public string GenerateToken(JwtPayload payload)
        {
            var now = DateTime.UtcNow;
            var expires = now.AddMinutes(_expirationMinutes);

            payload.IssuedAt = now;
            payload.ExpiresAt = expires;

            var claims = BuildClaims(payload, now);

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secretKey));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _issuer,
                audience: _audience,
                claims: claims,
                notBefore: now,
                expires: expires,
                signingCredentials: creds
            );

            var handler = new JwtSecurityTokenHandler();
            return handler.WriteToken(token);
        }

        public JwtPayload ValidateToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new ArgumentException("Token n\u00e3o pode ser vazio.", nameof(token));
            }

            var handler = new JwtSecurityTokenHandler();
            var parameters = GetValidationParameters(validateLifetime: true);

            var principal = handler.ValidateToken(token, parameters, out var securityToken);

            if (securityToken is not JwtSecurityToken jwtToken ||
                !jwtToken.Header.Alg.Equals(SecurityAlgorithms.HmacSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityTokenException("Token inv\u00e1lido.");
            }

            return BuildPayloadFromClaims(principal.Claims);
        }

        public string GenerateRefreshToken()
        {
            var randomNumber = RandomNumberGenerator.GetBytes(64);
            return Convert.ToBase64String(randomNumber);
        }

        public ClaimsPrincipal GetPrincipalFromExpiredToken(string token)
        {
            var handler = new JwtSecurityTokenHandler();
            var parameters = GetValidationParameters(validateLifetime: false);

            var principal = handler.ValidateToken(token, parameters, out var securityToken);

            if (securityToken is not JwtSecurityToken jwtToken ||
                !jwtToken.Header.Alg.Equals(SecurityAlgorithms.HmacSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityTokenException("Token inv\u00e1lido.");
            }

            return principal;
        }

        private TokenValidationParameters GetValidationParameters(bool validateLifetime)
        {
            return new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secretKey)),
                ValidateIssuer = true,
                ValidIssuer = _issuer,
                ValidateAudience = true,
                ValidAudience = _audience,
                ValidateLifetime = validateLifetime,
                ClockSkew = TimeSpan.FromMinutes(1)
            };
        }

        private static IEnumerable<Claim> BuildClaims(JwtPayload payload, DateTime issuedAt)
        {
            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Email, payload.Email ?? string.Empty),
                new Claim("name", payload.Nome ?? string.Empty),
                new Claim("is_super_admin", payload.IsSuperAdmin ? "true" : "false"),
                new Claim(JwtRegisteredClaimNames.Iat,
                    new DateTimeOffset(issuedAt).ToUnixTimeSeconds().ToString(),
                    ClaimValueTypes.Integer64)
            };

            if (payload.UserId.HasValue)
            {
                claims.Add(new Claim(JwtRegisteredClaimNames.Sub, payload.UserId.Value.ToString()));
            }

            if (payload.EstabelecimentoId.HasValue)
            {
                claims.Add(new Claim("estabelecimento_id", payload.EstabelecimentoId.Value.ToString()));
            }

            if (!string.IsNullOrWhiteSpace(payload.EstabelecimentoNome))
            {
                claims.Add(new Claim("estabelecimento_nome", payload.EstabelecimentoNome));
            }

            if (!string.IsNullOrWhiteSpace(payload.TipoEstabelecimento))
            {
                claims.Add(new Claim("tipo_estabelecimento", payload.TipoEstabelecimento));
            }

            if (!string.IsNullOrWhiteSpace(payload.TipoAcesso))
            {
                claims.Add(new Claim("tipo_acesso", payload.TipoAcesso));
            }

            if (payload.VinculoId.HasValue)
            {
                claims.Add(new Claim("vinculo_id", payload.VinculoId.Value.ToString()));
            }

            if (payload.Permissoes != null && payload.Permissoes.Count > 0)
            {
                var json = JsonSerializer.Serialize(payload.Permissoes);
                claims.Add(new Claim("permissoes", json));
            }

            claims.Add(new Claim("exp_at", payload.ExpiresAt.ToUniversalTime().ToString("O")));
            claims.Add(new Claim("iat_at", payload.IssuedAt.ToUniversalTime().ToString("O")));

            return claims;
        }

        private static JwtPayload BuildPayloadFromClaims(IEnumerable<Claim> claims)
        {
            var payload = new JwtPayload();
            var permissionsJson = string.Empty;

            foreach (var claim in claims)
            {
                switch (claim.Type)
                {
                    case JwtRegisteredClaimNames.Sub:
                        if (int.TryParse(claim.Value, out var userId))
                        {
                            payload.UserId = userId;
                        }
                        break;
                    case JwtRegisteredClaimNames.Email:
                        payload.Email = claim.Value;
                        break;
                    case "name":
                        payload.Nome = claim.Value;
                        break;
                    case "is_super_admin":
                        payload.IsSuperAdmin = string.Equals(claim.Value, "true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "estabelecimento_id":
                        if (Guid.TryParse(claim.Value, out var estId))
                        {
                            payload.EstabelecimentoId = estId;
                        }
                        break;
                    case "estabelecimento_nome":
                        payload.EstabelecimentoNome = claim.Value;
                        break;
                    case "tipo_estabelecimento":
                        payload.TipoEstabelecimento = claim.Value;
                        break;
                    case "tipo_acesso":
                        payload.TipoAcesso = claim.Value;
                        break;
                    case "vinculo_id":
                        if (Guid.TryParse(claim.Value, out var vinculoId))
                        {
                            payload.VinculoId = vinculoId;
                        }
                        break;
                    case "permissoes":
                        permissionsJson = claim.Value;
                        break;
                    case "exp_at":
                        if (DateTime.TryParse(claim.Value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var expAt))
                        {
                            payload.ExpiresAt = expAt;
                        }
                        break;
                    case "iat_at":
                        if (DateTime.TryParse(claim.Value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var iatAt))
                        {
                            payload.IssuedAt = iatAt;
                        }
                        break;
                }
            }

            if (!string.IsNullOrWhiteSpace(permissionsJson))
            {
                try
                {
                    var perms = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(permissionsJson);
                    payload.Permissoes = perms ?? new Dictionary<string, List<string>>();
                }
                catch
                {
                    payload.Permissoes = new Dictionary<string, List<string>>();
                }
            }
            else
            {
                payload.Permissoes = new Dictionary<string, List<string>>();
            }

            return payload;
        }
    }
}
