using System;
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
    [Route("api/crm/lancamentos")]
    public class CrmLancamentosController : CrmControllerBase
    {
        private readonly ICrmService _service;
        private readonly ILogger<CrmLancamentosController> _logger;

        public CrmLancamentosController(ICrmService service, ILogger<CrmLancamentosController> logger)
        {
            _service = service;
            _logger = logger;
        }

        [HttpPost]
        public async Task<IActionResult> Registrar([FromBody] RegistrarCrmLancamentoRequest request)
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;

            try
            {
                var response = await _service.RegistrarPagamentoAsync(CurrentUserId!.Value, request);
                return Ok(ApiResponse<CrmLancamentoDto>.Ok(response));
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
                _logger.LogError(ex, "Erro ao registrar lancamento do CRM");
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao registrar lancamento do CRM."));
            }
        }
    }
}
