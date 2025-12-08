using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Auth;
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
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(new
                {
                    success = false,
                    error = "Requisi\u00e7\u00e3o inv\u00e1lida."
                });
            }

            try
            {
                var response = await _authService.LoginAsync(request);
                return Ok(new { success = true, data = response });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(new { success = false, error = ex.Message });
            }
            catch
            {
                return StatusCode(500, new { success = false, error = "Erro ao processar login." });
            }
        }

        [HttpGet("oauth/google/url")]
        public async Task<IActionResult> ObterUrlLoginGoogle([FromQuery] string? redirectUri)
        {
            try
            {
                var response = await _authService.IniciarLoginGoogleAsync(redirectUri);
                return Ok(new { success = true, data = response });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { success = false, error = ex.Message });
            }
        }

        [HttpGet("oauth/google/callback")]
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
                return BadRequest(new { success = false, error = "Par\u00e2metros obrigat\u00f3rios n\u00e3o informados." });
            }

            try
            {
                var resultado = await _authService.ProcessarCallbackGoogleAsync(code, state);

                if (!string.IsNullOrWhiteSpace(resultado.RedirectUri) &&
                    TryBuildSuccessRedirect(resultado.RedirectUri!, resultado.Token, out var redirectSuccess))
                {
                    return Redirect(redirectSuccess!);
                }

                return Ok(new { success = true, data = resultado.Token });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, new { success = false, error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return await HandleGoogleErrorAsync(state, "invalid_request", ex.Message);
            }
            catch
            {
                return await HandleGoogleErrorAsync(state, "server_error", "N\u00e3o foi poss\u00edvel concluir o login social.");
            }
        }

        [HttpGet("estabelecimentos")]
        [Authorize]
        public async Task<IActionResult> ListarEstabelecimentos()
        {
            try
            {
                var userId = HttpContext.GetUserId();

                if (!userId.HasValue)
                {
                    return Unauthorized(new { success = false, error = "Usu\u00e1rio n\u00e3o autenticado." });
                }

                var estabelecimentos = await _authService.ListarEstabelecimentosDisponiveisAsync(userId.Value);
                return Ok(new { success = true, data = estabelecimentos });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(new { success = false, error = ex.Message });
            }
            catch
            {
                return StatusCode(500, new { success = false, error = "Erro ao listar estabelecimentos." });
            }
        }

        [HttpPost("selecionar-estabelecimento")]
        [Authorize]
        public async Task<IActionResult> SelecionarEstabelecimento([FromBody] SelecionarEstabelecimentoRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(new
                {
                    success = false,
                    error = "Requisi\u00e7\u00e3o inv\u00e1lida."
                });
            }

            try
            {
                var userId = HttpContext.GetUserId();

                if (!userId.HasValue)
                {
                    return Unauthorized(new { success = false, error = "Usu\u00e1rio n\u00e3o autenticado." });
                }

                var response = await _authService.SelecionarEstabelecimentoAsync(userId.Value, request.EstabelecimentoId);
                return Ok(new { success = true, data = response });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(new { success = false, error = ex.Message });
            }
            catch
            {
                return StatusCode(500, new { success = false, error = "Erro ao selecionar estabelecimento." });
            }
        }

        [HttpGet("me")]
        [Authorize]
        public IActionResult ObterUsuarioAtual()
        {
            var payload = HttpContext.GetJwtPayload();

            if (payload == null || payload.UserId == null)
            {
                return Unauthorized(new { success = false, error = "Usu\u00e1rio n\u00e3o autenticado." });
            }

            var data = new
            {
                Id = payload.UserId,
                Nome = payload.Nome,
                Email = payload.Email,
                IsSuperAdmin = payload.IsSuperAdmin,
                EstabelecimentoAtual = payload.EstabelecimentoId.HasValue
                    ? new
                    {
                        Id = payload.EstabelecimentoId.Value,
                        Nome = payload.EstabelecimentoNome,
                        Tipo = payload.TipoEstabelecimento,
                        TipoAcesso = payload.TipoAcesso
                    }
                    : null,
                Permissoes = payload.Permissoes
            };

            return Ok(new { success = true, data });
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

            var finalMessage = description ?? "Opera\u00e7\u00e3o cancelada.";
            return BadRequest(new { success = false, error = finalMessage });
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
    }
}
