using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Auth;
using APIBack.DTOs.Common;
using APIBack.DTOs.Gestao;
using APIBack.Model.Auth;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace APIBack.Controllers
{
    public sealed record TokenRequest(string? Token);
    public sealed record DefinirSenhaRequest(string? Token, string? Senha);
    public sealed record EsqueciSenhaRequest(string? Email);
    /// <summary>token vem nulo quando a senha foi gravada mas o login automatico nao foi possivel (ex.: loja sem vinculo ativo).</summary>
    public sealed record DefinirSenhaResponse(string Email, TokenResponse? Token, string? AvisoLogin);

    /// <summary>
    /// Primeiro acesso e redefinicao de senha por link. A Gestao gera o link (nao ha envio de e-mail) e o usuario define a
    /// propria senha. O token so trafega no corpo das chamadas, nunca na URL, e o banco guarda apenas o hash.
    /// </summary>
    [ApiController]
    public sealed class AcessoController : GestaoControllerBase
    {
        private readonly IAcessoService _acesso;
        private readonly IGestaoService _gestao;
        private readonly IAuthService _auth;
        private readonly ILogger<AcessoController> _logger;

        public AcessoController(IAcessoService acesso, IGestaoService gestao, IAuthService auth, ILogger<AcessoController> logger)
        {
            _acesso = acesso;
            _gestao = gestao;
            _auth = auth;
            _logger = logger;
        }

        [HttpGet("api/gestao/estabelecimentos/{id:guid}/acesso")]
        public async Task<IActionResult> Listar(Guid id)
        {
            if (!CurrentUserId.HasValue) return UnauthorizedResponse();
            if (!CurrentIsSuperAdmin) return ForbiddenResponse();
            var lista = await _acesso.ListarAsync(id, HttpContext.RequestAborted);
            return Ok(ApiResponse<IReadOnlyList<AcessoUsuarioDto>>.Ok(lista));
        }

        /// <summary>Cria o gerente da empresa de uma loja que ainda nao tem responsavel e devolve o link de primeiro acesso.</summary>
        [HttpPost("api/gestao/estabelecimentos/{id:guid}/acesso/responsavel")]
        public async Task<IActionResult> CriarResponsavel(Guid id, [FromBody] ResponsavelRequest request)
        {
            if (!CurrentUserId.HasValue) return UnauthorizedResponse();
            if (!CurrentIsSuperAdmin) return ForbiddenResponse();
            try
            {
                var link = await _gestao.CriarResponsavelAsync(
                    CurrentUserId.Value, CurrentEmpresaId, CurrentEstabelecimentoId, CurrentCompanyRole,
                    CurrentEstablishmentRole, CurrentIsSuperAdmin, id, request);
                return Ok(ApiResponse<AcessoLinkDto>.Ok(link));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(ex.Message));
            }
        }

        /// <summary>Gera um link novo (primeiro acesso se a senha ainda esta pendente, senao redefinicao). O link anterior deixa de valer.</summary>
        [HttpPost("api/gestao/estabelecimentos/{id:guid}/acesso/{usuarioId:int}/link")]
        public async Task<IActionResult> GerarLink(Guid id, int usuarioId)
        {
            if (!CurrentUserId.HasValue) return UnauthorizedResponse();
            if (!CurrentIsSuperAdmin) return ForbiddenResponse();
            try
            {
                var link = await _acesso.GerarLinkAsync(id, usuarioId, CurrentUserId, HttpContext.RequestAborted);
                return Ok(ApiResponse<AcessoLinkDto>.Ok(link));
            }
            catch (AcessoException ex)
            {
                return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Code));
            }
        }

        [AllowAnonymous]
        [HttpPost("api/auth/convite/consultar")]
        public async Task<IActionResult> Consultar([FromBody] TokenRequest request)
        {
            try
            {
                var info = await _acesso.ConsultarAsync(request.Token ?? string.Empty, HttpContext.RequestAborted);
                return Ok(ApiResponse<ConviteInfoDto>.Ok(info));
            }
            catch (AcessoException ex)
            {
                return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Code));
            }
        }

        [AllowAnonymous]
        [HttpPost("api/auth/convite/definir-senha")]
        public async Task<IActionResult> DefinirSenha([FromBody] DefinirSenhaRequest request)
        {
            string email;
            try
            {
                email = await _acesso.DefinirSenhaAsync(request.Token ?? string.Empty, request.Senha ?? string.Empty, HttpContext.RequestAborted);
            }
            catch (AcessoException ex)
            {
                return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Code));
            }

            // Entra direto quando possivel; se a loja estiver indisponivel a senha ja ficou salva e o motivo aparece no login.
            try
            {
                var token = await _auth.LoginAsync(new LoginRequest { Email = email, Senha = request.Senha!.Trim() });
                return Ok(ApiResponse<DefinirSenhaResponse>.Ok(new DefinirSenhaResponse(email, token, null)));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DeliveryDomainException)
            {
                _logger.LogInformation("Senha definida para {Email}, login automatico recusado: {Motivo}", email, ex.Message);
                return Ok(ApiResponse<DefinirSenhaResponse>.Ok(new DefinirSenhaResponse(email, null, ex.Message)));
            }
        }

        /// <summary>Sempre responde igual, exista o e-mail ou nao. O pedido aparece no bloco Acesso da loja para a Gestao enviar o link.</summary>
        [AllowAnonymous]
        [HttpPost("api/auth/esqueci-senha")]
        public async Task<IActionResult> EsqueciSenha([FromBody] EsqueciSenhaRequest request)
        {
            try
            {
                await _acesso.SolicitarRedefinicaoAsync(request.Email ?? string.Empty, HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao registrar pedido de redefinicao de senha.");
            }

            return Ok(ApiResponse<object>.Ok(new { registrado = true }));
        }
    }
}
