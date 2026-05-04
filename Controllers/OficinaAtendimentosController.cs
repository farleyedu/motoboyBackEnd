using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.Automation.Interfaces;
using APIBack.DTOs.Common;
using APIBack.DTOs.Configuracoes;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    [ApiController]
    [Route("api/oficina/atendimentos")]
    public class OficinaAtendimentosController : EstabelecimentoScopedControllerBase
    {
        private readonly IServicoAtendimentoRepository _repository;

        public OficinaAtendimentosController(IServicoAtendimentoRepository repository)
        {
            _repository = repository;
        }

        [HttpGet]
        [RequirePermission("Agendamentos", "visualizar")]
        public async Task<IActionResult> Listar([FromQuery] string? status, [FromQuery] int limite = 100)
        {
            if (!TryResolveCurrentEstabelecimentoAndModule("Agendamentos", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var itens = await _repository.ListarPorEstabelecimentoAsync(estabelecimentoId, status, limite);
            return Ok(ApiResponse<object>.Ok(new { itens }));
        }

        [HttpPatch("{id:guid}/status")]
        [RequirePermission("Agendamentos", "editar")]
        public async Task<IActionResult> AtualizarStatus(Guid id, [FromBody] AtualizarStatusRequest? request)
        {
            if (request == null)
            {
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            }

            if (!TryResolveCurrentEstabelecimentoAndModule("Agendamentos", out _, out var error))
            {
                return error!;
            }

            var status = request.Ativo ? "em_andamento" : "concluido";
            await _repository.AtualizarStatusAsync(id, status);
            return Ok(ApiResponse<object>.Ok(new { id, status }));
        }
    }
}
