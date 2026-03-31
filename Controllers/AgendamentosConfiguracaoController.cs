using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Agendamentos;
using APIBack.DTOs.Common;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    [ApiController]
    [Route("api/configuracoes/disponibilidade/config")]
    public class AgendamentosConfiguracaoController : EstabelecimentoScopedControllerBase
    {
        private readonly IEstabelecimentoAgendamentoConfigService _service;

        public AgendamentosConfiguracaoController(IEstabelecimentoAgendamentoConfigService service)
        {
            _service = service;
        }

        [HttpGet]
        [RequirePermission("Configuracoes", "visualizar")]
        public async Task<IActionResult> Obter()
        {
            if (!TryResolveCurrentEstabelecimentoAndModule("Disponibilidade", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var response = await _service.ObterAsync(estabelecimentoId);
            return Ok(ApiResponse<EstabelecimentoAgendamentoConfigDto>.Ok(response));
        }

        [HttpPut]
        [RequirePermission("Configuracoes", "editar")]
        public async Task<IActionResult> Salvar([FromBody] SalvarEstabelecimentoAgendamentoConfigRequest? request)
        {
            if (request == null)
            {
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            }

            if (!TryResolveCurrentEstabelecimentoAndModule("Disponibilidade", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var response = await _service.SalvarAsync(estabelecimentoId, request);
                return Ok(ApiResponse<EstabelecimentoAgendamentoConfigDto>.Ok(response));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
        }
    }
}
