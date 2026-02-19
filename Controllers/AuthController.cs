using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Auth;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Model.Auth;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace APIBack.Controllers
{
    [Route("api/auth")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;

        public AuthController(IAuthService authService)
        {
            _authService = authService;
        }

        [HttpPost("login")]
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        [ProducesResponseType(typeof(ApiResponse<TokenResponse>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<object>.Fail("Requisição inválida."));
            }

            try
            {
                var response = await _authService.LoginAsync(request);
                return Ok(ApiResponse<TokenResponse>.Ok(response));
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(ApiResponse<object>.Fail(ex.Message));
            }
            catch
            {
                return StatusCode(500, ApiResponse<object>.Fail("Erro ao processar login."));
            }
        }

        [HttpPost("refresh")]
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        [ProducesResponseType(typeof(ApiResponse<TokenResponse>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<object>.Fail("Requisição inválida."));
            }

            try
            {
                var response = await _authService.RefreshTokenAsync(request, GetClientIp(), GetUserAgent());
                return Ok(ApiResponse<TokenResponse>.Ok(response));
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(ApiResponse<object>.Fail(ex.Message));
            }
            catch
            {
                return StatusCode(500, ApiResponse<object>.Fail("Erro ao processar refresh token."));
            }
        }

        [HttpGet("oauth/google/url")]
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        public async Task<IActionResult> ObterUrlLoginGoogle([FromQuery] string? redirectUri)
        {
            try
            {
                var response = await _authService.IniciarLoginGoogleAsync(redirectUri);
                return Ok(ApiResponse<OAuthAuthorizationResponse>.Ok(response));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ApiResponse<object>.Fail(ex.Message));
            }
        }

        [HttpGet("oauth/google/callback")]
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        public async Task<IActionResult> GoogleOAuthCallback(
            [FromQuery] string? code,
            [FromQuery] string? state,
            [FromQuery] string? error,
            [FromQuery(Name = "error_description")] string? errorDescription)
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                return await HandleGoogleErrorAsync(state, error, errorDescription);
            }

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
            {
                return BadRequest(ApiResponse<object>.Fail("Parâmetros obrigatórios não informados."));
            }

            try
            {
                var resultado = await _authService.ProcessarCallbackGoogleAsync(code, state);

                if (!string.IsNullOrWhiteSpace(resultado.RedirectUri) &&
                    TryBuildSuccessRedirect(resultado.RedirectUri!, resultado.Token, out var redirectSuccess))
                {
                    return Redirect(redirectSuccess!);
                }

                return Ok(ApiResponse<TokenResponse>.Ok(resultado.Token));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.Fail(ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return await HandleGoogleErrorAsync(state, "invalid_request", ex.Message);
            }
            catch
            {
                return await HandleGoogleErrorAsync(state, "server_error", "Não foi possível concluir o login social.");
            }
        }

        [HttpGet("me")]
        [Authorize]
        [RequirePermission("Configuracoes", "visualizar")]
        [ProducesResponseType(typeof(ApiResponse<MeResponse>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
        public IActionResult ObterUsuarioAtual()
        {
            var payload = HttpContext.GetJwtPayload();

            if (payload == null || payload.UserId == null)
            {
                return Unauthorized(ApiResponse<object>.Fail("Usuário não autenticado."));
            }

            var data = new MeResponse
            {
                Id = payload.UserId.Value,
                Nome = payload.Nome ?? string.Empty,
                Email = payload.Email ?? string.Empty,
                IsSuperAdmin = payload.IsSuperAdmin,
                EstabelecimentoAtual = payload.EstabelecimentoId.HasValue
                    ? new MeEstabelecimentoAtualResponse
                    {
                        Id = payload.EstabelecimentoId.Value,
                        Nome = payload.EstabelecimentoNome ?? string.Empty,
                        Tipo = payload.TipoEstabelecimento ?? string.Empty,
                        TipoAcesso = payload.TipoAcesso
                    }
                    : null,
                Permissoes = payload.Permissoes ?? new Dictionary<string, List<string>>()
            };

            return Ok(ApiResponse<MeResponse>.Ok(data));
        }

        [HttpPost("logout")]
        [Authorize]
        public async Task<IActionResult> Logout([FromBody] LogoutRequest? request)
        {
            var userId = HttpContext.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized(ApiResponse<object>.Fail("Usuário não autenticado."));
            }

            try
            {
                await _authService.LogoutAsync(
                    userId.Value,
                    request ?? new LogoutRequest(),
                    GetClientIp(),
                    GetUserAgent());
                return NoContent();
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(ApiResponse<object>.Fail(ex.Message));
            }
            catch
            {
                return StatusCode(500, ApiResponse<object>.Fail("Erro ao processar logout."));
            }
        }

        private async Task<IActionResult> HandleGoogleErrorAsync(string? state, string error, string? description)
        {
            if (!string.IsNullOrWhiteSpace(state))
            {
                var redirect = await _authService.ConsumirRedirectGoogleAsync(state);

                if (!string.IsNullOrWhiteSpace(redirect) &&
                    TryBuildErrorRedirect(redirect!, error, description, out var redirectUrl))
                {
                    return Redirect(redirectUrl!);
                }
            }

            var finalMessage = description ?? "Operação cancelada.";
            return BadRequest(ApiResponse<object>.Fail(finalMessage));
        }

        private bool TryBuildSuccessRedirect(string redirectUri, TokenResponse token, out string? url)
        {
            var payload = new
            {
                success = true,
                token
            };

            return TryBuildRedirect(redirectUri, payload, out url);
        }

        private bool TryBuildErrorRedirect(string redirectUri, string error, string? description, out string? url)
        {
            var payload = new
            {
                success = false,
                error,
                errorDescription = description
            };

            return TryBuildRedirect(redirectUri, payload, out url);
        }

        private bool TryBuildRedirect(string redirectUri, object payload, out string? url)
        {
            url = null;

            try
            {
                var json = JsonSerializer.Serialize(payload);
                var encoded = Base64UrlEncode(Encoding.UTF8.GetBytes(json));
                url = QueryHelpers.AddQueryString(redirectUri, "payload", encoded);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string Base64UrlEncode(byte[] input)
        {
            return Convert.ToBase64String(input)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private string? GetClientIp()
        {
            return HttpContext.Connection.RemoteIpAddress?.ToString();
        }

        private string? GetUserAgent()
        {
            return Request.Headers.UserAgent.ToString();
        }
    }
}
