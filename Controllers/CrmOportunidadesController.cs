using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Common;
using APIBack.DTOs.CRM;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace APIBack.Controllers
{
    [ApiController]
    [Route("api/crm/oportunidades")]
    public class CrmOportunidadesController : CrmControllerBase
    {
        private readonly ICrmService _service;
        private readonly ILogger<CrmOportunidadesController> _logger;

        public CrmOportunidadesController(ICrmService service, ILogger<CrmOportunidadesController> logger)
        {
            _service = service;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Listar([FromQuery] string? status, [FromQuery] string? tipo, [FromQuery] int? responsavelUsuarioId, [FromQuery] string? busca)
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;

            try
            {
                var response = await _service.ListarOportunidadesAsync(status, tipo, responsavelUsuarioId, busca);
                return Ok(ApiResponse<IReadOnlyCollection<CrmOportunidadeResumoDto>>.Ok(response));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao listar oportunidades do CRM");
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao listar oportunidades do CRM."));
            }
        }

        [HttpGet("{oportunidadeId:guid}")]
        public async Task<IActionResult> Obter(Guid oportunidadeId)
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;

            try
            {
                var response = await _service.ObterOportunidadeAsync(oportunidadeId);
                return response == null
                    ? NotFound(ApiResponse<object>.Fail("Oportunidade nao encontrada."))
                    : Ok(ApiResponse<CrmOportunidadeDetalheDto>.Ok(response));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao obter oportunidade {OportunidadeId} do CRM", oportunidadeId);
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao obter oportunidade do CRM."));
            }
        }

        [HttpPost]
        public async Task<IActionResult> Criar([FromBody] CriarCrmOportunidadeRequest request)
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;

            try
            {
                var response = await _service.CriarOportunidadeAsync(CurrentUserId!.Value, request);
                return StatusCode(StatusCodes.Status201Created, ApiResponse<CrmOportunidadeResumoDto>.Ok(response));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao criar oportunidade do CRM");
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao criar oportunidade do CRM."));
            }
        }

        [HttpPut("{oportunidadeId:guid}")]
        public async Task<IActionResult> Atualizar(Guid oportunidadeId, [FromBody] AtualizarCrmOportunidadeRequest request)
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;

            try
            {
                var response = await _service.AtualizarOportunidadeAsync(CurrentUserId!.Value, oportunidadeId, request);
                return Ok(ApiResponse<CrmOportunidadeResumoDto>.Ok(response));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao atualizar oportunidade {OportunidadeId} do CRM", oportunidadeId);
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao atualizar oportunidade do CRM."));
            }
        }

        [HttpPost("{oportunidadeId:guid}/mover")]
        public async Task<IActionResult> Mover(Guid oportunidadeId, [FromBody] MoverCrmOportunidadeRequest request)
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;

            try
            {
                var response = await _service.MoverOportunidadeAsync(CurrentUserId!.Value, oportunidadeId, request);
                return Ok(ApiResponse<CrmOportunidadeResumoDto>.Ok(response));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao mover oportunidade {OportunidadeId} do CRM", oportunidadeId);
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao mover oportunidade do CRM."));
            }
        }

        /// <summary>Equipe Zippy que pode ser responsavel por oportunidade e contrato.</summary>
        [HttpGet("responsaveis")]
        public async Task<IActionResult> Responsaveis()
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;

            try
            {
                return Ok(ApiResponse<IReadOnlyCollection<CrmResponsavelDto>>.Ok(await _service.ListarResponsaveisAsync()));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao listar responsaveis do CRM");
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao listar responsaveis do CRM."));
            }
        }

        /// <summary>Empresas ja cadastradas com que o fechamento pode se ligar: CNPJ igual e nome parecido. O operador confirma qual e.</summary>
        [HttpGet("{oportunidadeId:guid}/candidatos-associacao")]
        public async Task<IActionResult> CandidatosAssociacao(Guid oportunidadeId, [FromQuery] string? cnpj, [FromQuery] string? nomeEmpresa)
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;

            try
            {
                var response = await _service.ListarCandidatosAssociacaoAsync(oportunidadeId, cnpj, nomeEmpresa);
                return Ok(ApiResponse<IReadOnlyCollection<CrmCandidatoAssociacaoDto>>.Ok(response));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao listar candidatos de associacao da oportunidade {OportunidadeId}", oportunidadeId);
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao listar candidatos de associacao."));
            }
        }

        [HttpPost("{oportunidadeId:guid}/fechar")]
        public async Task<IActionResult> Fechar(Guid oportunidadeId, [FromBody] FecharCrmOportunidadeRequest request)
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;

            try
            {
                var response = await _service.FecharOportunidadeAsync(CurrentUserId!.Value, oportunidadeId, request);
                return Ok(ApiResponse<FecharCrmOportunidadeResponse>.Ok(response));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao fechar oportunidade {OportunidadeId} do CRM", oportunidadeId);
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao fechar oportunidade do CRM."));
            }
        }
    }
}
