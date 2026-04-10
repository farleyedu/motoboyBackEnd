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
        public async Task<IActionResult> Listar([FromQuery] string? status, [FromQuery] string? tipo, [FromQuery] string? responsavel, [FromQuery] string? busca)
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;

            try
            {
                var response = await _service.ListarOportunidadesAsync(status, tipo, responsavel, busca);
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
