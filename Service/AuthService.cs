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
using APIBack.Options;
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

            // ⚠️ TEMPORÁRIO: Comparação direta de senhas (SEM BCrypt)
            // TODO: Trocar por BCrypt antes de ir para produção!
            if (string.IsNullOrWhiteSpace(usuario.Senha) ||
                !string.Equals(request.Senha, usuario.Senha, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("Usuário ou senha inválidos.");
            }

            // Super admin sem estabelecimento: token básico sem contexto de estabelecimento
            if (usuario.IsSuperAdmin && usuario.UltimoEstabelecimentoAcessado == null)
            {
                return await BuildTokenResponseAsync(connection, usuario, null);
            }

            var estabelecimentoId = usuario.UltimoEstabelecimentoAcessado
                                 ?? await ObterPrimeiroEstabelecimentoDoUsuarioAsync(connection, usuario.Id);

            return await BuildTokenResponseAsync(connection, usuario, estabelecimentoId);
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

            Guid? estabelecimentoId = null;

            if (!(usuario.IsSuperAdmin && usuario.UltimoEstabelecimentoAcessado == null))
            {
                estabelecimentoId = usuario.UltimoEstabelecimentoAcessado
                    ?? await ObterPrimeiroEstabelecimentoDoUsuarioAsync(connection, usuario.Id);
            }

            var token = await BuildTokenResponseAsync(connection, usuario, estabelecimentoId);

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

            var senha = GenerateRandomPassword();

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

        private async Task<UsuarioDb?> ObterUsuarioPorIdAsync(NpgsqlConnection connection, Guid userId)
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
 WHERE id = @UserId";

            return await connection.QueryFirstOrDefaultAsync<UsuarioDb>(sql, new { UserId = userId });
        }

        private async Task<Guid> ObterPrimeiroEstabelecimentoDoUsuarioAsync(NpgsqlConnection connection, int userId)
        {
            const string sql = @"
SELECT id_estabelecimento
  FROM usuario_estabelecimentos
 WHERE id_usuario = @UserId
   AND ativo = TRUE
   AND status = 'ativo'
 ORDER BY data_criacao ASC
 LIMIT 1";

            var estabelecimentoId = await connection.ExecuteScalarAsync<Guid?>(sql, new { UserId = userId });

            if (!estabelecimentoId.HasValue)
            {
                throw new UnauthorizedAccessException("Usuário não possui vínculo ativo com estabelecimentos.");
            }

            return estabelecimentoId.Value;
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
                var contexto = await ObterContextoEstabelecimentoAsync(connection, usuario.Id, estabelecimentoId.Value, usuario.IsSuperAdmin);

                if (!usuario.IsSuperAdmin && contexto.VinculoId == null)
                {
                    throw new UnauthorizedAccessException("Usuário não possui acesso ao estabelecimento selecionado.");
                }

                var permissoes = !string.IsNullOrWhiteSpace(contexto.TipoAcesso)
                    ? await ObterPermissoesAsync(connection, contexto.TipoAcesso)
                    : new Dictionary<string, List<string>>();

                payload = new JwtPayload
                {
                    UserId = usuario.Id,
                    Nome = usuario.Nome ?? string.Empty,
                    Email = usuario.Email ?? string.Empty,
                    IsSuperAdmin = usuario.IsSuperAdmin,
                    EstabelecimentoId = contexto.EstabelecimentoId,
                    EstabelecimentoNome = contexto.EstabelecimentoNome,
                    TipoEstabelecimento = contexto.TipoEstabelecimento,
                    TipoAcesso = contexto.TipoAcesso,
                    VinculoId = contexto.VinculoId,
                    Permissoes = permissoes
                };

                estabelecimentoInfo = new EstabelecimentoInfo
                {
                    Id = contexto.EstabelecimentoId,
                    Nome = contexto.EstabelecimentoNome ?? string.Empty,
                    Tipo = contexto.TipoEstabelecimento ?? string.Empty,
                    TipoAcesso = contexto.TipoAcesso ?? string.Empty
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
        e.nome_fantasia         AS EstabelecimentoNome,
        te.nome                 AS TipoEstabelecimento,
        ue.id                   AS VinculoId,
        ue.tipo_acesso          AS TipoAcesso
  FROM estabelecimentos e
  JOIN tipo_estabelecimento te ON te.id = e.id_tipo_estabelecimento
  LEFT JOIN usuario_estabelecimentos ue 
         ON ue.id_estabelecimento = e.id 
        AND ue.id_usuario = @UserId 
        AND ue.ativo = TRUE
        AND ue.status = 'ativo'
 WHERE e.id = @EstabelecimentoId";

            var contexto = await connection.QueryFirstOrDefaultAsync<EstabelecimentoContextoDb>(sql, new
            {
                UserId = userId,
                EstabelecimentoId = estabelecimentoId
            });

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

        private async Task<Dictionary<string, List<string>>> ObterPermissoesAsync(
            NpgsqlConnection connection,
            string? tipoAcesso)
        {
            // Apenas permissões padrão - sem customizadas
            if (string.IsNullOrWhiteSpace(tipoAcesso))
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }

            const string sqlPadrao = @"
SELECT  m.nome                  AS Modulo,
        pp.permissoes::text     AS Permissoes
  FROM permissoes_padrao pp
  JOIN modulos_disponiveis m ON m.id = pp.id_modulo
 WHERE pp.tipo_acesso = @TipoAcesso";

            var linhas = await connection.QueryAsync<PermissaoRow>(sqlPadrao, new { TipoAcesso = tipoAcesso });

            var permissoes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var linha in linhas)
            {
                if (!string.IsNullOrWhiteSpace(linha.Modulo))
                {
                    permissoes[linha.Modulo] = ParsePermissoes(linha.Permissoes);
                }
            }

            return permissoes;
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
            public Guid EstabelecimentoId { get; set; }
            public string? EstabelecimentoNome { get; set; }
            public string? TipoEstabelecimento { get; set; }
            public Guid? VinculoId { get; set; }
            public string? TipoAcesso { get; set; }
        }

        private sealed class PermissaoRow
        {
            public string Modulo { get; set; } = string.Empty;
            public string? Permissoes { get; set; }
        }

    }
}


