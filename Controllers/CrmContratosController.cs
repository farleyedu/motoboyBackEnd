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
    [Route("api/crm/contratos")]
    public class CrmContratosController : CrmControllerBase
    {
        private readonly ICrmService _service;
        private readonly ILogger<CrmContratosController> _logger;

        public CrmContratosController(ICrmService service, ILogger<CrmContratosController> logger)
        {
            _service = service;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Listar([FromQuery] string? status)
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;

            try
            {
                var response = await _service.ListarContratosAsync(status);
                return Ok(ApiResponse<IReadOnlyCollection<CrmContratoResumoDto>>.Ok(response));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao listar contratos do CRM");
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao listar contratos do CRM."));
            }
        }

        [HttpGet("{contratoId:guid}")]
        public async Task<IActionResult> Obter(Guid contratoId)
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;

            try
            {
                var response = await _service.ObterContratoAsync(contratoId);
                return response == null
                    ? NotFound(ApiResponse<object>.Fail("Contrato nao encontrado."))
                    : Ok(ApiResponse<CrmContratoDetalheDto>.Ok(response));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao obter contrato {ContratoId} do CRM", contratoId);
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao obter contrato do CRM."));
            }
        }

        [HttpPut("{contratoId:guid}")]
        public async Task<IActionResult> Atualizar(Guid contratoId, [FromBody] AtualizarCrmContratoRequest request)
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;

            try
            {
                var response = await _service.AtualizarContratoAsync(CurrentUserId!.Value, contratoId, request);
                return Ok(ApiResponse<CrmContratoResumoDto>.Ok(response));
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
                _logger.LogError(ex, "Erro ao atualizar contrato {ContratoId} do CRM", contratoId);
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao atualizar contrato do CRM."));
            }
        }
    }
}
