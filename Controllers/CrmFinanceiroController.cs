using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Common;
using APIBack.DTOs.CRM;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace APIBack.Controllers
{
    [ApiController]
    [Route("api/crm/financeiro")]
    public class CrmFinanceiroController : CrmControllerBase
    {
        private readonly ICrmService _service;
        private readonly ILogger<CrmFinanceiroController> _logger;

        public CrmFinanceiroController(ICrmService service, ILogger<CrmFinanceiroController> logger)
        {
            _service = service;
            _logger = logger;
        }

        [HttpGet("resumo")]
        public async Task<IActionResult> Resumo([FromQuery] DateTime? referenciaMes)
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;

            try
            {
                var response = await _service.ObterResumoFinanceiroAsync(referenciaMes);
                return Ok(ApiResponse<CrmFinanceiroResumoDto>.Ok(response));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao obter resumo financeiro do CRM");
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao obter resumo financeiro do CRM."));
            }
        }

        [HttpGet("lancamentos")]
        public async Task<IActionResult> Lancamentos([FromQuery] DateTime? referenciaMes)
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;

            try
            {
                var response = await _service.ListarLancamentosFinanceiroAsync(referenciaMes);
                return Ok(ApiResponse<IReadOnlyCollection<CrmLancamentoDto>>.Ok(response));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao listar lancamentos financeiros do CRM");
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao listar lancamentos financeiros do CRM."));
            }
        }

        [HttpGet("meses-ativos")]
        public async Task<IActionResult> MesesAtivos()
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;

            try
            {
                var response = await _service.ListarMesesAtivosAsync();
                return Ok(ApiResponse<IReadOnlyCollection<string>>.Ok(response));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao listar meses ativos do CRM");
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao listar meses ativos."));
            }
        }

        [HttpGet("divisao")]
        public async Task<IActionResult> Divisao([FromQuery] DateTime? referenciaMes)
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;

            try
            {
                var response = await _service.ObterDivisaoFinanceiraAsync(referenciaMes);
                return Ok(ApiResponse<CrmDivisaoFinanceiraDto>.Ok(response));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao obter divisao financeira do CRM");
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao obter divisao financeira do CRM."));
            }
        }
    }
}
