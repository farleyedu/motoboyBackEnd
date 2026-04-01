using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using APIBack.DTOs.Auth;
using APIBack.Model.Auth;
using APIBack.Model.Gestao;
using APIBack.Options;
using APIBack.Security;
using APIBack.Service.Interface;
using Dapper;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.Service
{
    public class AuthService : IAuthService
    {
        private readonly string _connectionString;
        private readonly IConfiguration _configuration;
        private readonly IJwtService _jwtService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _memoryCache;
        private readonly GoogleOAuthOptions _googleOAuthOptions;
        private readonly TimeSpan _oauthStateLifetime;
        private readonly ILogger<AuthService> _logger;
        private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
        private const string GoogleProvider = "google";
        private const string GoogleAuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
        private const string GoogleTokenEndpoint = "https://oauth2.googleapis.com/token";
        private const string GoogleUserInfoEndpoint = "https://www.googleapis.com/oauth2/v3/userinfo";
        private const string StateCachePrefix = "oauth:google:";
        private static readonly string[] DefaultGoogleScopes = { "openid", "profile", "email" };

        public AuthService(
            IConfiguration configuration,
            IJwtService jwtService,
            IHttpClientFactory httpClientFactory,
            IMemoryCache memoryCache,
            IOptions<GoogleOAuthOptions> googleOAuthOptions,
            ILogger<AuthService> logger)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _jwtService = jwtService ?? throw new ArgumentNullException(nameof(jwtService));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
            _googleOAuthOptions = googleOAuthOptions?.Value ?? new GoogleOAuthOptions();
            if (_googleOAuthOptions.Scopes == null || _googleOAuthOptions.Scopes.Count == 0)
            {
                _googleOAuthOptions.Scopes = new List<string>(DefaultGoogleScopes);
            }

            _googleOAuthOptions.AllowedPostLoginRedirects ??= new List<string>();
            var ttlMinutes = _googleOAuthOptions.StateTtlMinutes <= 0 ? 5 : _googleOAuthOptions.StateTtlMinutes;
            _oauthStateLifetime = TimeSpan.FromMinutes(ttlMinutes);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                               ?? configuration["ConnectionStrings:DefaultConnection"]
                               ?? throw new InvalidOperationException("Connection string 'DefaultConnection' n\u00e3o encontrada.");
        }

        public async Task<TokenResponse> LoginAsync(LoginRequest request)
        {
            await using var connection = new NpgsqlConnection(_connectionString);

            const string sqlUsuario = @"
SELECT id,
       nome,
       email,
       senha,
       is_super_admin,
       ultimo_estabelecimento_acessado,
       provider,
       provider_id,
       deleted_at
  FROM usuario
 WHERE LOWER(email) = LOWER(@Email)
   AND deleted_at IS NULL";

            var usuario = await connection.QueryFirstOrDefaultAsync<UsuarioDb>(sqlUsuario, new { request.Email });

            if (usuario == null)
            {
                throw new UnauthorizedAccessException("Usuário ou senha inválidos.");
            }

            if (string.IsNullOrWhiteSpace(usuario.Senha) ||
                !PasswordSecurity.Verify(request.Senha, usuario.Senha))
            {
                throw new UnauthorizedAccessException("Usuário ou senha inválidos.");
            }

            if (!PasswordSecurity.IsHash(usuario.Senha))
            {
                usuario.Senha = PasswordSecurity.Hash(request.Senha);
                await AtualizarSenhaUsuarioAsync(connection, usuario.Id, usuario.Senha);
            }

            // Super admin sem estabelecimento: token básico sem contexto de estabelecimento
            var estabelecimentoId = await ResolverEstabelecimentoParaTokenAsync(connection, usuario);

            var response = await BuildTokenResponseAsync(connection, usuario, estabelecimentoId);
            await PersistRefreshTokenAsync(connection, usuario.Id, response.RefreshToken, null, null);
            return response;
        }

        public async Task<TokenResponse> RefreshTokenAsync(
            RefreshTokenRequest request,
            string? ipAddress,
            string? userAgent)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.RefreshToken))
            {
                throw new UnauthorizedAccessException("Refresh token inválido.");
            }

            var rawRefreshToken = request.RefreshToken.Trim();
            var refreshTokenHash = HashRefreshToken(rawRefreshToken);

            await using var connection = new NpgsqlConnection(_connectionString);
            var tokenAtual = await ObterRefreshTokenAsync(connection, refreshTokenHash);

            if (tokenAtual == null)
            {
                throw new UnauthorizedAccessException("Refresh token inválido.");
            }

            if (tokenAtual.RevokedAt.HasValue)
            {
                throw new UnauthorizedAccessException("Refresh token já foi revogado.");
            }

            if (tokenAtual.ExpiresAt <= DateTime.UtcNow)
            {
                await RevokeRefreshTokenAsync(connection, tokenAtual.Id, ipAddress, null, "expired");
                throw new UnauthorizedAccessException("Refresh token expirado.");
            }

            var usuario = await ObterUsuarioPorIdAsync(connection, tokenAtual.UserId);
            if (usuario == null || usuario.DeletedAt.HasValue)
            {
                await RevokeRefreshTokenAsync(connection, tokenAtual.Id, ipAddress, null, "user_not_found");
                throw new UnauthorizedAccessException("Usuário não encontrado para refresh token.");
            }

            var estabelecimentoId = await ResolverEstabelecimentoParaTokenAsync(connection, usuario);

            var response = await BuildTokenResponseAsync(connection, usuario, estabelecimentoId);
            var newRefreshTokenHash = HashRefreshToken(response.RefreshToken);
            var novoRefreshTokenId = await PersistRefreshTokenAsync(connection, usuario.Id, response.RefreshToken, ipAddress, userAgent);

            var rotacaoConcluida = await RevokeRefreshTokenAsync(
                connection,
                tokenAtual.Id,
                ipAddress,
                newRefreshTokenHash,
                "rotated");

            if (!rotacaoConcluida)
            {
                await RevokeRefreshTokenAsync(connection, novoRefreshTokenId, ipAddress, null, "rotation_conflict_cleanup");
                throw new UnauthorizedAccessException("Não foi possível concluir a rotação do refresh token.");
            }

            return response;
        }

        public async Task LogoutAsync(int userId, LogoutRequest request, string? ipAddress, string? userAgent)
        {
            if (userId <= 0)
            {
                throw new UnauthorizedAccessException("Usuário inválido.");
            }

            request ??= new LogoutRequest();
            _logger.LogInformation("Logout solicitado para usuário {UserId}. LogoutAll={LogoutAll} UserAgent={UserAgent}",
                userId, request.LogoutFromAllDevices, userAgent);

            await using var connection = new NpgsqlConnection(_connectionString);

            if (request.LogoutFromAllDevices || string.IsNullOrWhiteSpace(request.RefreshToken))
            {
                await RevokeAllRefreshTokensAsync(connection, userId, ipAddress, "logout");
                return;
            }

            var refreshTokenHash = HashRefreshToken(request.RefreshToken.Trim());
            var token = await ObterRefreshTokenAsync(connection, refreshTokenHash);

            if (token == null || token.UserId != userId)
            {
                return;
            }

            await RevokeRefreshTokenAsync(connection, token.Id, ipAddress, null, "logout");
        }

        public Task<OAuthAuthorizationResponse> IniciarLoginGoogleAsync(string? redirectUri)
        {
            var options = EnsureGoogleOAuthConfigurada();
            var effectiveRedirect = ResolveRedirectUri(redirectUri);
            var state = GenerateStateValue();
            var codeVerifier = GenerateCodeVerifier();
            var codeChallenge = ComputeCodeChallenge(codeVerifier);
            var scopes = options.Scopes != null && options.Scopes.Count > 0
                ? string.Join(' ', options.Scopes)
                : string.Join(' ', DefaultGoogleScopes);

            var query = new Dictionary<string, string?>
            {
                ["client_id"] = options.ClientId,
                ["redirect_uri"] = options.RedirectUri,
                ["response_type"] = "code",
                ["scope"] = scopes,
                ["state"] = state,
                ["access_type"] = string.IsNullOrWhiteSpace(options.AccessType) ? "online" : options.AccessType,
                ["include_granted_scopes"] = "true",
                ["code_challenge"] = codeChallenge,
                ["code_challenge_method"] = "S256"
            };

            if (!string.IsNullOrWhiteSpace(options.Prompt))
            {
                query["prompt"] = options.Prompt;
            }

            var authorizationUrl = QueryHelpers.AddQueryString(GoogleAuthEndpoint, query!);

            var entry = new OAuthStateCacheEntry
            {
                CodeVerifier = codeVerifier,
                RedirectUri = effectiveRedirect,
                CreatedAt = DateTimeOffset.UtcNow
            };

            _memoryCache.Set(BuildStateCacheKey(state), entry, _oauthStateLifetime);

            var response = new OAuthAuthorizationResponse
            {
                AuthorizationUrl = authorizationUrl,
                State = state,
                ExpiresAt = entry.CreatedAt.Add(_oauthStateLifetime)
            };

            return Task.FromResult(response);
        }

        public async Task<OAuthCallbackResult> ProcessarCallbackGoogleAsync(string code, string state)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new ArgumentException("C\u00f3digo n\u00e3o informado.", nameof(code));
            }

            if (string.IsNullOrWhiteSpace(state))
            {
                throw new ArgumentException("Estado n\u00e3o informado.", nameof(state));
            }

            var options = EnsureGoogleOAuthConfigurada();
            var stateEntry = TryTakeStateEntry(state);

            if (stateEntry == null || string.IsNullOrWhiteSpace(stateEntry.CodeVerifier))
            {
                throw new InvalidOperationException("Estado OAuth inv\u00e1lido ou expirado.");
            }

            var tokenResponse = await ExchangeCodeForTokensAsync(options, code, stateEntry.CodeVerifier);
            var profile = await FetchGoogleProfileAsync(tokenResponse.AccessToken);

            await using var connection = new NpgsqlConnection(_connectionString);
            var usuario = await GarantirContaGoogleAsync(connection, profile);

            var estabelecimentoId = await ResolverEstabelecimentoParaTokenAsync(connection, usuario);

            var token = await BuildTokenResponseAsync(connection, usuario, estabelecimentoId);
            await PersistRefreshTokenAsync(connection, usuario.Id, token.RefreshToken, null, null);

            return new OAuthCallbackResult
            {
                Token = token,
                RedirectUri = stateEntry.RedirectUri
            };
        }

        public Task<string?> ConsumirRedirectGoogleAsync(string state)
        {
            if (string.IsNullOrWhiteSpace(state))
            {
                return Task.FromResult<string?>(null);
            }

            var entry = TryTakeStateEntry(state);
            return Task.FromResult(entry?.RedirectUri);
        }

        private GoogleOAuthOptions EnsureGoogleOAuthConfigurada()
        {
            if (_googleOAuthOptions == null || !_googleOAuthOptions.Enabled)
            {
                throw new InvalidOperationException("Login social com Google est\u00e1 desabilitado.");
            }

            if (string.IsNullOrWhiteSpace(_googleOAuthOptions.ClientId) ||
                string.IsNullOrWhiteSpace(_googleOAuthOptions.ClientSecret) ||
                string.IsNullOrWhiteSpace(_googleOAuthOptions.RedirectUri))
            {
                throw new InvalidOperationException("Configura\u00e7\u00f5es do Google OAuth est\u00e3o incompletas.");
            }

            if (_googleOAuthOptions.AllowedPostLoginRedirects == null ||
                _googleOAuthOptions.AllowedPostLoginRedirects.Count == 0)
            {
                throw new InvalidOperationException("Nenhum redirect permitido foi configurado para o login social.");
            }

            return _googleOAuthOptions;
        }

        private string ResolveRedirectUri(string? redirectUri)
        {
            if (!string.IsNullOrWhiteSpace(redirectUri) && IsRedirectAllowed(redirectUri))
            {
                return redirectUri;
            }

            if (!string.IsNullOrWhiteSpace(_googleOAuthOptions.DefaultPostLoginRedirect) &&
                IsRedirectAllowed(_googleOAuthOptions.DefaultPostLoginRedirect))
            {
                return _googleOAuthOptions.DefaultPostLoginRedirect!;
            }

            throw new InvalidOperationException("Redirect informado n\u00e3o \u00e9 permitido.");
        }

        private bool IsRedirectAllowed(string? redirectUri)
        {
            if (string.IsNullOrWhiteSpace(redirectUri) ||
                _googleOAuthOptions.AllowedPostLoginRedirects == null)
            {
                return false;
            }

            foreach (var allowed in _googleOAuthOptions.AllowedPostLoginRedirects)
            {
                if (string.IsNullOrWhiteSpace(allowed))
                {
                    continue;
                }

                if (redirectUri.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GenerateStateValue()
        {
            var random = RandomNumberGenerator.GetBytes(32);
            return Base64UrlEncode(random);
        }

        private static string GenerateCodeVerifier()
        {
            var random = RandomNumberGenerator.GetBytes(64);
            return Base64UrlEncode(random);
        }

        private static string ComputeCodeChallenge(string codeVerifier)
        {
            using var sha = SHA256.Create();
            var inputBytes = Encoding.ASCII.GetBytes(codeVerifier);
            var hash = sha.ComputeHash(inputBytes);
            return Base64UrlEncode(hash);
        }

        private static string Base64UrlEncode(byte[] input)
        {
            return Convert.ToBase64String(input)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static string BuildStateCacheKey(string state) => $"{StateCachePrefix}{state}";

        private OAuthStateCacheEntry? TryTakeStateEntry(string state)
        {
            var cacheKey = BuildStateCacheKey(state);

            if (_memoryCache.TryGetValue<OAuthStateCacheEntry>(cacheKey, out var entry))
            {
                _memoryCache.Remove(cacheKey);
                return entry;
            }

            return null;
        }

        private async Task<GoogleTokenResponse> ExchangeCodeForTokensAsync(
            GoogleOAuthOptions options,
            string code,
            string codeVerifier)
        {
            var payload = new Dictionary<string, string?>
            {
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
                ["code"] = code,
                ["redirect_uri"] = options.RedirectUri,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = codeVerifier
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, GoogleTokenEndpoint)
            {
                Content = new FormUrlEncodedContent(payload!)
            };

            var client = _httpClientFactory.CreateClient(GoogleProvider);
            var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Falha ao trocar c\u00f3digo do Google: {Status} - {Body}", response.StatusCode, content);
                throw new InvalidOperationException("N\u00e3o foi poss\u00edvel autenticar com o Google.");
            }

            try
            {
                var token = JsonSerializer.Deserialize<GoogleTokenResponse>(content, _jsonOptions);

                if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
                {
                    throw new InvalidOperationException("Resposta do Google n\u00e3o retornou um access token.");
                }

                return token;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Erro ao analisar resposta de token do Google");
                throw new InvalidOperationException("Resposta do Google inv\u00e1lida.");
            }
        }

        private async Task<GoogleUserInfo> FetchGoogleProfileAsync(string accessToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GoogleUserInfoEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var client = _httpClientFactory.CreateClient(GoogleProvider);
            var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Falha ao obter perfil Google: {Status} - {Body}", response.StatusCode, content);
                throw new InvalidOperationException("N\u00e3o foi poss\u00edvel confirmar os dados da conta Google.");
            }

            try
            {
                var profile = JsonSerializer.Deserialize<GoogleUserInfo>(content, _jsonOptions)
                              ?? throw new InvalidOperationException("Perfil do Google inv\u00e1lido.");

                if (string.IsNullOrWhiteSpace(profile.Sub))
                {
                    throw new InvalidOperationException("Google n\u00e3o retornou o identificador da conta.");
                }

                if (string.IsNullOrWhiteSpace(profile.Email) || !profile.EmailVerified)
                {
                    throw new InvalidOperationException("Somente contas com e-mail Google verificado podem ser usadas.");
                }

                return profile;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Erro ao desserializar perfil retornado pelo Google.");
                throw new InvalidOperationException("N\u00e3o foi poss\u00edvel interpretar o perfil retornado pelo Google.");
            }
        }

        private async Task<UsuarioDb> GarantirContaGoogleAsync(NpgsqlConnection connection, GoogleUserInfo profile)
        {
            const string sqlPorProvider = @"
SELECT id,
       nome,
       email,
       senha,
       is_super_admin,
       ultimo_estabelecimento_acessado,
       provider,
       provider_id,
       deleted_at
  FROM usuario
 WHERE provider = @Provider
   AND provider_id = @ProviderId
   AND deleted_at IS NULL
 LIMIT 1";

            var usuario = await connection.QueryFirstOrDefaultAsync<UsuarioDb>(sqlPorProvider, new
            {
                Provider = GoogleProvider,
                ProviderId = profile.Sub
            });

            if (usuario != null)
            {
                return usuario;
            }

            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync();
            }

            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            usuario = await connection.QueryFirstOrDefaultAsync<UsuarioDb>(
                sqlPorProvider + " FOR UPDATE",
                new { Provider = GoogleProvider, ProviderId = profile.Sub },
                transaction);

            if (usuario != null)
            {
                await transaction.CommitAsync();
                return usuario;
            }

            const string sqlPorEmail = @"
SELECT id,
       nome,
       email,
       senha,
       is_super_admin,
       ultimo_estabelecimento_acessado,
       provider,
       provider_id,
       deleted_at
  FROM usuario
 WHERE LOWER(email) = LOWER(@Email)
   AND deleted_at IS NULL
 ORDER BY id
 LIMIT 1
 FOR UPDATE";

            var usuarioPorEmail = await connection.QueryFirstOrDefaultAsync<UsuarioDb>(
                sqlPorEmail,
                new { Email = profile.Email },
                transaction);

            if (usuarioPorEmail != null)
            {
                const string sqlUpdate = @"
UPDATE usuario
   SET provider = @Provider,
       provider_id = @ProviderId,
       nome = COALESCE(@Nome, nome),
       updated_at = NOW()
 WHERE id = @UsuarioId";

                await connection.ExecuteAsync(sqlUpdate, new
                {
                    Provider = GoogleProvider,
                    ProviderId = profile.Sub,
                    Nome = profile.Name,
                    UsuarioId = usuarioPorEmail.Id
                }, transaction);

                usuarioPorEmail.Provider = GoogleProvider;
                usuarioPorEmail.ProviderId = profile.Sub;

                if (!string.IsNullOrWhiteSpace(profile.Name))
                {
                    usuarioPorEmail.Nome = profile.Name!;
                }

                await transaction.CommitAsync();
                return usuarioPorEmail;
            }

            var senha = PasswordSecurity.Hash(GenerateRandomPassword());

            const string sqlInsert = @"
INSERT INTO usuario (nome,
                     email,
                     senha,
                     is_super_admin,
                     ultimo_estabelecimento_acessado,
                     provider,
                     provider_id,
                     created_at,
                     updated_at)
VALUES (@Nome,
        @Email,
        @Senha,
        FALSE,
        NULL,
        @Provider,
        @ProviderId,
        NOW(),
        NOW())
RETURNING id";

            var novoId = await connection.ExecuteScalarAsync<int>(sqlInsert, new
            {
                Nome = profile.Name ?? profile.Email ?? "Conta Google",
                Email = profile.Email,
                Senha = senha,
                Provider = GoogleProvider,
                ProviderId = profile.Sub
            }, transaction);

            await transaction.CommitAsync();

            return new UsuarioDb
            {
                Id = novoId,
                Nome = profile.Name ?? profile.Email ?? "Conta Google",
                Email = profile.Email ?? string.Empty,
                Senha = senha,
                IsSuperAdmin = false,
                UltimoEstabelecimentoAcessado = null,
                Provider = GoogleProvider,
                ProviderId = profile.Sub,
                DeletedAt = null
            };
        }

        private static string GenerateRandomPassword()
        {
            var random = RandomNumberGenerator.GetBytes(48);
            return Base64UrlEncode(random);
        }

        private DateTime GetRefreshTokenExpirationUtc()
        {
            var jwtSection = _configuration.GetSection("Jwt");
            var refreshTokenExpirationDays = int.TryParse(jwtSection["RefreshTokenExpirationDays"], out var days)
                ? days
                : 7;

            return DateTime.UtcNow.AddDays(refreshTokenExpirationDays);
        }

        private static string HashRefreshToken(string refreshToken)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(refreshToken);
            var hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash);
        }

        private async Task<long> PersistRefreshTokenAsync(
            NpgsqlConnection connection,
            int userId,
            string rawRefreshToken,
            string? ipAddress,
            string? userAgent)
        {
            var tokenHash = HashRefreshToken(rawRefreshToken);
            var expiresAt = GetRefreshTokenExpirationUtc();

            const string sql = @"
INSERT INTO usuario_refresh_tokens (id_usuario,
                                    token_hash,
                                    expires_at,
                                    created_at,
                                    created_by_ip,
                                    user_agent)
VALUES (@UserId,
        @TokenHash,
        @ExpiresAt,
        NOW(),
        @CreatedByIp,
        @UserAgent)
RETURNING id";

            return await connection.ExecuteScalarAsync<long>(sql, new
            {
                UserId = userId,
                TokenHash = tokenHash,
                ExpiresAt = expiresAt,
                CreatedByIp = ipAddress,
                UserAgent = userAgent
            });
        }

        private async Task<RefreshTokenDb?> ObterRefreshTokenAsync(NpgsqlConnection connection, string tokenHash)
        {
            const string sql = @"
SELECT id,
       id_usuario              AS UserId,
       token_hash              AS TokenHash,
       expires_at              AS ExpiresAt,
       created_at              AS CreatedAt,
       revoked_at              AS RevokedAt,
       revoked_by_ip           AS RevokedByIp,
       replaced_by_token_hash  AS ReplacedByTokenHash,
       reason_revoked          AS ReasonRevoked
  FROM usuario_refresh_tokens
 WHERE token_hash = @TokenHash
 LIMIT 1";

            return await connection.QueryFirstOrDefaultAsync<RefreshTokenDb>(sql, new { TokenHash = tokenHash });
        }

        private async Task<int> RevokeAllRefreshTokensAsync(
            NpgsqlConnection connection,
            int userId,
            string? ipAddress,
            string reason)
        {
            const string sql = @"
UPDATE usuario_refresh_tokens
   SET revoked_at = NOW(),
       revoked_by_ip = @RevokedByIp,
       reason_revoked = @Reason
 WHERE id_usuario = @UserId
   AND revoked_at IS NULL";

            return await connection.ExecuteAsync(sql, new
            {
                UserId = userId,
                RevokedByIp = ipAddress,
                Reason = reason
            });
        }

        private async Task<bool> RevokeRefreshTokenAsync(
            NpgsqlConnection connection,
            long tokenId,
            string? ipAddress,
            string? replacedByTokenHash,
            string reason)
        {
            const string sql = @"
UPDATE usuario_refresh_tokens
   SET revoked_at = NOW(),
       revoked_by_ip = @RevokedByIp,
       replaced_by_token_hash = COALESCE(@ReplacedByTokenHash, replaced_by_token_hash),
       reason_revoked = @Reason
 WHERE id = @Id
   AND revoked_at IS NULL";

            var affectedRows = await connection.ExecuteAsync(sql, new
            {
                Id = tokenId,
                RevokedByIp = ipAddress,
                ReplacedByTokenHash = replacedByTokenHash,
                Reason = reason
            });

            return affectedRows > 0;
        }

        private async Task<UsuarioDb?> ObterUsuarioPorIdAsync(NpgsqlConnection connection, int userId)
        {
            const string sql = @"
SELECT id,
       nome,
       email,
       senha,
       is_super_admin,
       ultimo_estabelecimento_acessado,
       provider,
       provider_id,
       deleted_at
  FROM usuario
 WHERE id = @UserId
   AND deleted_at IS NULL";

            return await connection.QueryFirstOrDefaultAsync<UsuarioDb>(sql, new { UserId = userId });
        }

        private async Task<Guid> ObterPrimeiroEstabelecimentoDoUsuarioAsync(NpgsqlConnection connection, int userId)
        {
            var estabelecimentoId = await TentarObterPrimeiroEstabelecimentoAsync(
                connection,
                userId,
                "ORDER BY data_criacao ASC");

            if (!estabelecimentoId.HasValue)
            {
                estabelecimentoId = await TentarObterPrimeiroEstabelecimentoAsync(
                    connection,
                    userId,
                    "ORDER BY created_at ASC");
            }

            if (!estabelecimentoId.HasValue)
            {
                estabelecimentoId = await TentarObterPrimeiroEstabelecimentoAsync(
                    connection,
                    userId,
                    "ORDER BY id ASC",
                    suppressUndefinedColumnLog: true);
            }

            if (!estabelecimentoId.HasValue)
            {
                throw new UnauthorizedAccessException("Usuario nao possui vinculo ativo com estabelecimentos.");
            }

            return estabelecimentoId.Value;
        }

        private async Task<Guid?> ResolverEstabelecimentoParaTokenAsync(NpgsqlConnection connection, UsuarioDb usuario)
        {
            if (usuario.IsSuperAdmin)
            {
                return usuario.UltimoEstabelecimentoAcessado;
            }

            return usuario.UltimoEstabelecimentoAcessado
                ?? await ObterPrimeiroEstabelecimentoDoUsuarioAsync(connection, usuario.Id);
        }

        private async Task<Guid?> TentarObterPrimeiroEstabelecimentoAsync(
            NpgsqlConnection connection,
            int userId,
            string orderByClause,
            bool suppressUndefinedColumnLog = false)
        {
            var sql = $@"
SELECT id_estabelecimento
  FROM usuario_estabelecimentos
 WHERE id_usuario = @UserId
   AND ativo = TRUE
   AND status = 'ativo'
 {orderByClause}
 LIMIT 1";

            try
            {
                return await connection.ExecuteScalarAsync<Guid?>(sql, new { UserId = userId });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedColumn)
            {
                if (!suppressUndefinedColumnLog)
                {
                    _logger.LogWarning(
                        "Coluna de ordenacao ausente em usuario_estabelecimentos ao buscar primeiro estabelecimento. UserId={UserId} Clause={OrderByClause}",
                        userId,
                        orderByClause);
                }

                return null;
            }
        }

        private async Task<TokenResponse> BuildTokenResponseAsync(
            NpgsqlConnection connection,
            UsuarioDb usuario,
            Guid? estabelecimentoId)
        {
            JwtPayload payload;
            EstabelecimentoInfo? estabelecimentoInfo = null;

            if (estabelecimentoId.HasValue)
            {
                var contexto = await ObterContextoEstabelecimentoAsync(
                    connection,
                    usuario.Id,
                    estabelecimentoId.Value,
                    usuario.IsSuperAdmin);

                if (!usuario.IsSuperAdmin && contexto.VinculoId == null)
                {
                    throw new UnauthorizedAccessException("Usuário não possui acesso ao estabelecimento selecionado.");
                }

                var permissoes = await ObterPermissoesAsync(
                    connection,
                    contexto.TipoAcessoEmpresa,
                    contexto.TipoAcesso,
                    contexto.PermissoesCustomizadas,
                    contexto.EstabelecimentoId);

                payload = new JwtPayload
                {
                    UserId = usuario.Id,
                    Nome = usuario.Nome ?? string.Empty,
                    Email = usuario.Email ?? string.Empty,
                    IsSuperAdmin = usuario.IsSuperAdmin,
                    EmpresaId = contexto.EmpresaId,
                    EmpresaNome = contexto.EmpresaNome,
                    TipoAcessoEmpresa = contexto.TipoAcessoEmpresa,
                    EmpresaVinculoId = contexto.EmpresaVinculoId,
                    EstabelecimentoId = contexto.EstabelecimentoId,
                    EstabelecimentoNome = contexto.EstabelecimentoNome,
                    TipoEstabelecimento = contexto.TipoEstabelecimento,
                    EstabelecimentoModulosAtivos = ResolveUiModules(contexto.EstabelecimentoNome, contexto.ModulosAtivosRaw),
                    TipoAcesso = string.IsNullOrWhiteSpace(contexto.TipoAcesso) ? null : contexto.TipoAcesso,
                    VinculoId = contexto.VinculoId,
                    Permissoes = permissoes
                };

                var tipoAcessoTela = RoleCatalog.ResolveScreenRole(
                    contexto.TipoAcessoEmpresa,
                    contexto.TipoAcesso,
                    usuario.IsSuperAdmin);

                estabelecimentoInfo = new EstabelecimentoInfo
                {
                    Id = contexto.EstabelecimentoId,
                    Nome = contexto.EstabelecimentoNome ?? string.Empty,
                    Tipo = contexto.TipoEstabelecimento ?? string.Empty,
                    ModulosAtivos = ResolveUiModules(contexto.EstabelecimentoNome, contexto.ModulosAtivosRaw),
                    TipoAcesso = tipoAcessoTela
                };
            }
            else
            {
                payload = new JwtPayload
                {
                    UserId = usuario.Id,
                    Nome = usuario.Nome ?? string.Empty,
                    Email = usuario.Email ?? string.Empty,
                    IsSuperAdmin = usuario.IsSuperAdmin,
                    Permissoes = new Dictionary<string, List<string>>()
                };
            }

            var jwtSection = _configuration.GetSection("Jwt");
            var expirationMinutes = int.TryParse(jwtSection["ExpirationMinutes"], out var minutes)
                ? minutes
                : 60;

            var accessToken = _jwtService.GenerateToken(payload);
            var refreshToken = _jwtService.GenerateRefreshToken();

            return new TokenResponse
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresIn = expirationMinutes * 60,
                User = new UserInfo
                {
                    Id = usuario.Id,
                    Nome = usuario.Nome ?? string.Empty,
                    Email = usuario.Email ?? string.Empty,
                    IsSuperAdmin = usuario.IsSuperAdmin,
                    EmpresaAtual = payload.EmpresaId.HasValue
                        ? new EmpresaInfo
                        {
                            Id = payload.EmpresaId.Value,
                            Nome = payload.EmpresaNome ?? string.Empty,
                            TipoAcesso = payload.TipoAcessoEmpresa
                        }
                        : null,
                    EstabelecimentoAtual = estabelecimentoInfo
                }
            };
        }

        private async Task<EstabelecimentoContextoDb> ObterContextoEstabelecimentoAsync(
            NpgsqlConnection connection,
            int userId,
            Guid estabelecimentoId,
            bool isSuperAdmin)
        {
            const string sql = @"
SELECT  e.id                    AS EstabelecimentoId,
        e.id_empresa            AS EmpresaId,
        emp.nome_fantasia       AS EmpresaNome,
        e.nome_fantasia         AS EstabelecimentoNome,
        te.nome                 AS TipoEstabelecimento,
        e.modulos_ativos::text[] AS ModulosAtivosRaw,
        ue.id                   AS VinculoId,
        ue.tipo_acesso          AS TipoAcesso,
        ue.permissoes_customizadas::text AS PermissoesCustomizadas,
        uemp.id                 AS EmpresaVinculoId,
        COALESCE(uemp.tipo_acesso, 'colaborador') AS TipoAcessoEmpresa
  FROM estabelecimentos e
  JOIN empresas emp ON emp.id = e.id_empresa
  JOIN tipo_estabelecimento te ON te.id = e.id_tipo_estabelecimento
  LEFT JOIN usuario_estabelecimentos ue 
         ON ue.id_estabelecimento = e.id 
        AND ue.id_usuario = @UserId 
        AND ue.ativo = TRUE
        AND ue.status = 'ativo'
  LEFT JOIN usuario_empresas uemp
         ON uemp.id_empresa = e.id_empresa
        AND uemp.id_usuario = @UserId
        AND uemp.ativo = TRUE
        AND uemp.status = 'ativo'
 WHERE e.id = @EstabelecimentoId";

            EstabelecimentoContextoDb? contexto;

            try
            {
                contexto = await connection.QueryFirstOrDefaultAsync<EstabelecimentoContextoDb>(sql, new
                {
                    UserId = userId,
                    EstabelecimentoId = estabelecimentoId
                });
            }
            catch (PostgresException ex) when (EhTabelaUsuarioEmpresasAusente(ex))
            {
                _logger.LogWarning(
                    "Tabela usuario_empresas ausente ao montar contexto do estabelecimento. Aplicando fallback legado. UserId={UserId} EstabelecimentoId={EstabelecimentoId}",
                    userId,
                    estabelecimentoId);

                contexto = await ObterContextoEstabelecimentoLegadoAsync(connection, userId, estabelecimentoId);
            }

            if (contexto == null)
            {
                throw new UnauthorizedAccessException("Estabelecimento não encontrado.");
            }

            if (!isSuperAdmin && contexto.VinculoId == null)
            {
                throw new UnauthorizedAccessException("Usuário não possui vínculo ativo com este estabelecimento.");
            }

            return contexto;
        }

        private async Task<EstabelecimentoContextoDb?> ObterContextoEstabelecimentoLegadoAsync(
            NpgsqlConnection connection,
            int userId,
            Guid estabelecimentoId)
        {
            const string sql = @"
SELECT  e.id                    AS EstabelecimentoId,
        e.id_empresa            AS EmpresaId,
        emp.nome_fantasia       AS EmpresaNome,
        e.nome_fantasia         AS EstabelecimentoNome,
        te.nome                 AS TipoEstabelecimento,
        e.modulos_ativos::text[] AS ModulosAtivosRaw,
        ue.id                   AS VinculoId,
        ue.tipo_acesso          AS TipoAcesso,
        ue.permissoes_customizadas::text AS PermissoesCustomizadas,
        NULL::uuid              AS EmpresaVinculoId,
        NULL::varchar           AS TipoAcessoEmpresa
  FROM estabelecimentos e
  JOIN empresas emp ON emp.id = e.id_empresa
  JOIN tipo_estabelecimento te ON te.id = e.id_tipo_estabelecimento
  LEFT JOIN usuario_estabelecimentos ue
         ON ue.id_estabelecimento = e.id
        AND ue.id_usuario = @UserId
        AND ue.ativo = TRUE
        AND ue.status = 'ativo'
 WHERE e.id = @EstabelecimentoId";

            return await connection.QueryFirstOrDefaultAsync<EstabelecimentoContextoDb>(sql, new
            {
                UserId = userId,
                EstabelecimentoId = estabelecimentoId
            });
        }

        private static bool EhTabelaUsuarioEmpresasAusente(PostgresException ex)
        {
            if (ex.SqlState != PostgresErrorCodes.UndefinedTable)
            {
                return false;
            }

            if (string.Equals(ex.TableName, "usuario_empresas", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return ex.MessageText?.IndexOf("usuario_empresas", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task<Dictionary<string, List<string>>> ObterPermissoesAsync(
            NpgsqlConnection connection,
            string? tipoAcessoEmpresa,
            string? tipoAcessoEstabelecimento,
            string? permissoesCustomizadas,
            Guid estabelecimentoId)
        {
            var permissoes = ParsePermissoesCustomizadas(permissoesCustomizadas);
            var modulosUi = new List<string>();

            if (estabelecimentoId != Guid.Empty)
            {
                var modulosAtivos = await ObterModulosAtivosAsync(connection, estabelecimentoId);
                modulosUi = ResolveUiModules(string.Empty, modulosAtivos.ToArray());
                if (modulosAtivos.Count > 0)
                {
                    var interseccao = permissoes.Keys
                        .Where(k => modulosAtivos.Contains(k))
                        .ToList();

                    // Safety check: se houver ao menos um módulo em comum, filtrar pelo ativo.
                    if (interseccao.Count > 0)
                    {
                        permissoes = permissoes
                            .Where(kvp => modulosAtivos.Contains(kvp.Key))
                            .ToDictionary(
                                kvp => kvp.Key,
                                kvp => kvp.Value,
                                StringComparer.OrdinalIgnoreCase);
                    }
                }
            }

            return CardapioPermissionBridge.Apply(permissoes, modulosUi);
        }

        private static Dictionary<string, List<string>> ParsePermissoesCustomizadas(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                var result = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(raw,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return result ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private async Task<HashSet<string>> ObterModulosAtivosAsync(NpgsqlConnection connection, Guid estabelecimentoId)
        {
            const string sql = @"
SELECT unnest(modulos_ativos)::text
  FROM estabelecimentos
 WHERE id = @EstabelecimentoId
   AND (ativo IS NULL OR ativo = TRUE)";

            var modulos = await connection.QueryAsync<string>(sql, new { EstabelecimentoId = estabelecimentoId });
            return modulos
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static async Task AtualizarSenhaUsuarioAsync(
            NpgsqlConnection connection,
            int userId,
            string senhaHash)
        {
            const string sql = @"
UPDATE usuario
   SET senha = @Senha,
       updated_at = NOW()
 WHERE id = @UserId";

            await connection.ExecuteAsync(sql, new { UserId = userId, Senha = senhaHash });
        }

        private static List<string> ResolveUiModules(string? establishmentName, string[]? rawModules)
        {
            return EstabelecimentoModuleMapper.ToUiModules(establishmentName ?? string.Empty, rawModules);
        }

        private static void ApplyCustomPermissions(
            Dictionary<string, List<string>> permissoesBase,
            string? permissoesCustomizadasRaw)
        {
            if (string.IsNullOrWhiteSpace(permissoesCustomizadasRaw))
            {
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(permissoesCustomizadasRaw);
                var root = doc.RootElement;

                // Compatibilidade legada: array simples de ações aplicada em todos os módulos já existentes.
                if (root.ValueKind == JsonValueKind.Array)
                {
                    var acoesGlobais = ReadActions(root);
                    if (acoesGlobais.Count == 0)
                    {
                        return;
                    }

                    foreach (var modulo in permissoesBase.Keys.ToList())
                    {
                        var lista = permissoesBase[modulo];
                        foreach (var acao in acoesGlobais)
                        {
                            if (!lista.Any(x => string.Equals(x, acao, StringComparison.OrdinalIgnoreCase)))
                            {
                                lista.Add(acao);
                            }
                        }
                    }

                    return;
                }

                if (root.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                var grants = ReadModuleMap(root, "grants")
                             ?? ReadModuleMap(root, "allow")
                             ?? ReadModuleMap(root, "permissoes")
                             ?? ReadModuleMap(root, "permissions");

                // Compatibilidade: objeto simples { "whatsapp": ["visualizar"] }.
                if (grants == null)
                {
                    grants = ReadDirectModuleMap(root);
                }

                var revokes = ReadModuleMap(root, "revokes")
                              ?? ReadModuleMap(root, "deny");

                if (grants != null)
                {
                    foreach (var kvp in grants)
                    {
                        if (!permissoesBase.TryGetValue(kvp.Key, out var existentes))
                        {
                            existentes = new List<string>();
                            permissoesBase[kvp.Key] = existentes;
                        }

                        foreach (var acao in kvp.Value)
                        {
                            if (!existentes.Any(x => string.Equals(x, acao, StringComparison.OrdinalIgnoreCase)))
                            {
                                existentes.Add(acao);
                            }
                        }
                    }
                }

                if (revokes != null)
                {
                    foreach (var kvp in revokes)
                    {
                        if (!permissoesBase.TryGetValue(kvp.Key, out var existentes) || existentes.Count == 0)
                        {
                            continue;
                        }

                        existentes.RemoveAll(acao =>
                            kvp.Value.Any(rev => string.Equals(rev, acao, StringComparison.OrdinalIgnoreCase)));
                    }
                }
            }
            catch
            {
                // Silencioso por compatibilidade com payloads legados malformados.
            }
        }

        private static Dictionary<string, List<string>>? ReadModuleMap(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in node.EnumerateObject())
            {
                var modulo = prop.Name?.Trim();
                if (string.IsNullOrWhiteSpace(modulo))
                {
                    continue;
                }

                result[modulo] = ReadActions(prop.Value);
            }

            return result;
        }

        private static Dictionary<string, List<string>>? ReadDirectModuleMap(JsonElement root)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in root.EnumerateObject())
            {
                var key = prop.Name?.Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (string.Equals(key, "grants", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "revokes", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "allow", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "deny", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "permissions", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "permissoes", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (prop.Value.ValueKind != JsonValueKind.Array &&
                    prop.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                result[key] = ReadActions(prop.Value);
            }

            return result.Count > 0 ? result : null;
        }

        private static List<string> ReadActions(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.String)
            {
                var single = node.GetString();
                return string.IsNullOrWhiteSpace(single)
                    ? new List<string>()
                    : new List<string> { single.Trim() };
            }

            if (node.ValueKind != JsonValueKind.Array)
            {
                return new List<string>();
            }

            var list = new List<string>();
            foreach (var item in node.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var action = item.GetString();
                if (!string.IsNullOrWhiteSpace(action))
                {
                    list.Add(action.Trim());
                }
            }

            return list
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> ParsePermissoes(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new List<string>();
            }

            raw = raw.Trim();

            try
            {
                if (raw.StartsWith("[", StringComparison.Ordinal))
                {
                    var list = JsonSerializer.Deserialize<List<string>>(raw);
                    if (list != null)
                    {
                        return list
                            .Where(p => !string.IsNullOrWhiteSpace(p))
                            .Select(p => p.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }
                }
            }
            catch
            {
                // Ignora erros de parse de JSON e tenta fallback
            }

            var separadores = new[] { ',', ';', '|' };

            return raw
                .Split(separadores, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private sealed class OAuthStateCacheEntry
        {
            public string CodeVerifier { get; set; } = string.Empty;
            public string RedirectUri { get; set; } = string.Empty;
            public DateTimeOffset CreatedAt { get; set; }
        }

        private sealed class GoogleTokenResponse
        {
            [JsonPropertyName("access_token")]
            public string AccessToken { get; set; } = string.Empty;

            [JsonPropertyName("expires_in")]
            public int ExpiresIn { get; set; }

            [JsonPropertyName("refresh_token")]
            public string? RefreshToken { get; set; }

            [JsonPropertyName("scope")]
            public string? Scope { get; set; }

            [JsonPropertyName("token_type")]
            public string? TokenType { get; set; }

            [JsonPropertyName("id_token")]
            public string? IdToken { get; set; }
        }

        private sealed class GoogleUserInfo
        {
            [JsonPropertyName("sub")]
            public string? Sub { get; set; }

            [JsonPropertyName("email")]
            public string? Email { get; set; }

            [JsonPropertyName("email_verified")]
            public bool EmailVerified { get; set; }

            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("given_name")]
            public string? GivenName { get; set; }

            [JsonPropertyName("family_name")]
            public string? FamilyName { get; set; }

            [JsonPropertyName("picture")]
            public string? Picture { get; set; }
        }

        private sealed class UsuarioDb
        {
            public int Id { get; set; }
            public string Nome { get; set; } = string.Empty;
            public string Email { get; set; } = string.Empty;
            public string Senha { get; set; } = string.Empty;
            public bool IsSuperAdmin { get; set; }
            public Guid? UltimoEstabelecimentoAcessado { get; set; }
            public string? Provider { get; set; }
            public string? ProviderId { get; set; }
            public DateTime? DeletedAt { get; set; }
        }

        private sealed class EstabelecimentoContextoDb
        {
            public Guid EmpresaId { get; set; }
            public string? EmpresaNome { get; set; }
            public Guid? EmpresaVinculoId { get; set; }
            public string? TipoAcessoEmpresa { get; set; }
            public Guid EstabelecimentoId { get; set; }
            public string? EstabelecimentoNome { get; set; }
            public string? TipoEstabelecimento { get; set; }
            public string[]? ModulosAtivosRaw { get; set; }
            public Guid? VinculoId { get; set; }
            public string? TipoAcesso { get; set; }
            public string? PermissoesCustomizadas { get; set; }
        }

        private sealed class RefreshTokenDb
        {
            public long Id { get; set; }
            public int UserId { get; set; }
            public string TokenHash { get; set; } = string.Empty;
            public DateTime ExpiresAt { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime? RevokedAt { get; set; }
            public string? RevokedByIp { get; set; }
            public string? ReplacedByTokenHash { get; set; }
            public string? ReasonRevoked { get; set; }
        }

        private sealed class PermissaoRow
        {
            public string Modulo { get; set; } = string.Empty;
            public string? Permissoes { get; set; }
        }

    }
}


