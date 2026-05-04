using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Agendamentos;
using APIBack.DTOs.Common;
using APIBack.DTOs.Configuracoes;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    [ApiController]
    [Route("api/oficina/agendamentos")]
    public class OficinaAgendamentosController : EstabelecimentoScopedControllerBase
    {
        private readonly IOficinaAgendamentoService _service;

        public OficinaAgendamentosController(IOficinaAgendamentoService service)
        {
            _service = service;
        }

        [HttpGet]
        [RequirePermission("Agendamentos", "visualizar")]
        public async Task<IActionResult> Listar(
            [FromQuery] DateTime? dataInicio,
            [FromQuery] DateTime? dataFim,
            [FromQuery] string? status,
            [FromQuery] long? profissionalId,
            [FromQuery] Guid? servicoId)
        {
            if (!TryResolveCurrentEstabelecimentoAndModule("Agendamentos", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var inicio = (dataInicio ?? DateTime.Today).Date;
            var fim = (dataFim ?? inicio).Date;
            var itens = await _service.ListarPorPeriodoAsync(estabelecimentoId, inicio, fim, status, profissionalId, servicoId);
            return Ok(ApiResponse<object>.Ok(new { itens }));
        }

        [HttpPost]
        [RequirePermission("Agendamentos", "criar")]
        public async Task<IActionResult> Criar([FromBody] CriarOficinaAgendamentoRequest? request)
        {
            if (request == null)
            {
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            }

            if (!TryResolveCurrentEstabelecimentoAndModule("Agendamentos", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var clienteId = request.DadosExtras != null &&
                            request.DadosExtras.TryGetValue("clienteId", out var rawClienteId) &&
                            Guid.TryParse(Convert.ToString(rawClienteId), out var parsedClienteId)
                ? parsedClienteId
                : Guid.Empty;

            var telefone = request.DadosExtras != null && request.DadosExtras.TryGetValue("telefoneE164", out var rawTelefone)
                ? Convert.ToString(rawTelefone) ?? string.Empty
                : string.Empty;

            if (clienteId == Guid.Empty)
            {
                return BadRequestErrorResponse("clienteId e obrigatorio em dadosExtras para criacao manual.");
            }

            try
            {
                var item = await _service.CriarAsync(estabelecimentoId, clienteId, telefone, request);
                return Ok(ApiResponse<OficinaAgendamentoDto>.Ok(item));
            }
            catch (InvalidOperationException ex)
            {
                return ConflictErrorResponse(ex.Message);
            }
        }

        [HttpPatch("{id:guid}/remarcar")]
        [RequirePermission("Agendamentos", "editar")]
        public async Task<IActionResult> Remarcar(Guid id, [FromBody] RemarcarOficinaAgendamentoRequest? request)
        {
            if (request == null)
            {
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            }

            if (!TryResolveCurrentEstabelecimentoAndModule("Agendamentos", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var item = await _service.RemarcarAsync(estabelecimentoId, id, request);
                return Ok(ApiResponse<OficinaAgendamentoDto>.Ok(item));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFoundErrorResponse(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return ConflictErrorResponse(ex.Message);
            }
        }

        [HttpPatch("{id:guid}/cancelar")]
        [RequirePermission("Agendamentos", "cancelar")]
        public async Task<IActionResult> Cancelar(Guid id, [FromBody] AtualizarStatusRequest? request)
        {
            if (!TryResolveCurrentEstabelecimentoAndModule("Agendamentos", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var item = await _service.CancelarAsync(estabelecimentoId, id, request?.Ativo == false ? "cancelado pelo painel" : null);
                return Ok(ApiResponse<OficinaAgendamentoDto>.Ok(item));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFoundErrorResponse(ex.Message);
            }
        }
    }
}
