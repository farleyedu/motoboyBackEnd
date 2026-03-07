using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Automation.Controllers
{
    [ApiController]
    [Route("garagem/leads")]
    public class GaragemLeadsController : ControllerBase
    {
        private static readonly HashSet<string> StatusLeadPermitidos = new(StringComparer.OrdinalIgnoreCase)
        {
            "pendente",
            "em_andamento",
            "concluido",
            "cancelado"
        };

        private static readonly HashSet<string> StatusSimulacaoPermitidos = new(StringComparer.OrdinalIgnoreCase)
        {
            "rascunho",
            "enviada",
            "aprovada",
            "recusada",
            "expirada"
        };

        private readonly IGaragemPainelRepository _repository;

        public GaragemLeadsController(IGaragemPainelRepository repository)
        {
            _repository = repository;
        }

        [HttpGet]
        [RequirePermission("Garagem", "visualizar")]
        public async Task<IActionResult> Listar(
            [FromQuery] string? busca,
            [FromQuery] string? status,
            [FromQuery] string? objetivo,
            [FromQuery] int pagina = 1,
            [FromQuery] int tamanhoPagina = 20,
            [FromQuery] Guid? idEstabelecimento = null)
        {
            if (!TryResolveEstabelecimento(idEstabelecimento, out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var paginaNormalizada = pagina < 1 ? 1 : pagina;
            var tamanhoNormalizado = Math.Clamp(tamanhoPagina <= 0 ? 20 : tamanhoPagina, 1, 200);

            var (itens, total) = await _repository.ListarLeadsAsync(
                estabelecimentoId,
                busca,
                status,
                objetivo,
                paginaNormalizada,
                tamanhoNormalizado);

            var contagens = await _repository.ContarLeadsPorStatusAsync(estabelecimentoId, busca, objetivo);
            var totaisPorStatus = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["pendente"] = 0,
                ["em_andamento"] = 0,
                ["concluido"] = 0,
                ["cancelado"] = 0
            };

            foreach (var count in contagens)
            {
                if (!string.IsNullOrWhiteSpace(count.Status))
                {
                    totaisPorStatus[count.Status] = count.Total;
                }
            }

            var response = new GarageLeadListResponseDto
            {
                Pagina = paginaNormalizada,
                TamanhoPagina = tamanhoNormalizado,
                Total = total,
                TotaisPorStatus = totaisPorStatus,
                Itens = itens
            };

            return Ok(response);
        }

        [HttpGet("{idLead:guid}")]
        [RequirePermission("Garagem", "visualizar")]
        public async Task<IActionResult> ObterDetalhe(Guid idLead, [FromQuery] Guid? idEstabelecimento = null)
        {
            if (!TryResolveEstabelecimento(idEstabelecimento, out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var detalhe = await _repository.ObterLeadDetalheAsync(estabelecimentoId, idLead);
            if (detalhe == null)
            {
                return NotFound();
            }

            return Ok(detalhe);
        }

        [HttpPatch("{idLead:guid}/status")]
        [RequirePermission("Garagem", "editar")]
        public async Task<IActionResult> AtualizarStatus(Guid idLead, [FromBody] UpdateGarageLeadStatusRequest request, [FromQuery] Guid? idEstabelecimento = null)
        {
            if (!TryResolveEstabelecimento(idEstabelecimento, out var estabelecimentoId, out var error))
            {
                return error!;
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Status))
            {
                return BadRequest(new { success = false, error = "Status e obrigatorio." });
            }

            var statusNormalizado = request.Status.Trim().ToLowerInvariant();
            if (!StatusLeadPermitidos.Contains(statusNormalizado))
            {
                return BadRequest(new { success = false, error = "Status de lead invalido." });
            }

            var atualizado = await _repository.AtualizarStatusLeadAsync(estabelecimentoId, idLead, statusNormalizado);
            if (!atualizado)
            {
                return NotFound();
            }

            var detalhe = await _repository.ObterLeadDetalheAsync(estabelecimentoId, idLead);
            return detalhe == null ? Ok() : Ok(detalhe);
        }

        [HttpPost("{idLead:guid}/simulacoes")]
        [RequirePermission("Garagem", "criar")]
        public async Task<IActionResult> CriarSimulacao(Guid idLead, [FromBody] CreateGarageLeadSimulationRequest request, [FromQuery] Guid? idEstabelecimento = null)
        {
            if (!TryResolveEstabelecimento(idEstabelecimento, out var estabelecimentoId, out var error))
            {
                return error!;
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Titulo))
            {
                return BadRequest(new { success = false, error = "Titulo e obrigatorio." });
            }

            if (!string.IsNullOrWhiteSpace(request.Status) &&
                !StatusSimulacaoPermitidos.Contains(request.Status.Trim().ToLowerInvariant()))
            {
                return BadRequest(new { success = false, error = "Status de simulacao invalido." });
            }

            var simulacao = await _repository.CriarSimulacaoAsync(estabelecimentoId, idLead, request);
            if (simulacao == null)
            {
                return NotFound(new { success = false, error = "Lead nao encontrado para o estabelecimento informado." });
            }

            return StatusCode(201, simulacao);
        }

        [HttpPut("{idLead:guid}/simulacoes/{idSimulacao:guid}")]
        [RequirePermission("Garagem", "editar")]
        public async Task<IActionResult> AtualizarSimulacao(
            Guid idLead,
            Guid idSimulacao,
            [FromBody] UpdateGarageLeadSimulationRequest request,
            [FromQuery] Guid? idEstabelecimento = null)
        {
            if (!TryResolveEstabelecimento(idEstabelecimento, out var estabelecimentoId, out var error))
            {
                return error!;
            }

            if (request == null)
            {
                return BadRequest(new { success = false, error = "Corpo da requisicao e obrigatorio." });
            }

            if (!string.IsNullOrWhiteSpace(request.Status) &&
                !StatusSimulacaoPermitidos.Contains(request.Status.Trim().ToLowerInvariant()))
            {
                return BadRequest(new { success = false, error = "Status de simulacao invalido." });
            }

            var simulacao = await _repository.AtualizarSimulacaoAsync(estabelecimentoId, idLead, idSimulacao, request);
            if (simulacao == null)
            {
                return NotFound();
            }

            return Ok(simulacao);
        }

        [HttpDelete("{idLead:guid}/simulacoes/{idSimulacao:guid}")]
        [RequirePermission("Garagem", "deletar")]
        public async Task<IActionResult> RemoverSimulacao(
            Guid idLead,
            Guid idSimulacao,
            [FromQuery] Guid? idEstabelecimento = null)
        {
            if (!TryResolveEstabelecimento(idEstabelecimento, out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var removido = await _repository.RemoverSimulacaoAsync(estabelecimentoId, idLead, idSimulacao);
            if (!removido)
            {
                return NotFound();
            }

            return NoContent();
        }

        private bool TryResolveEstabelecimento(Guid? requestedId, out Guid effectiveId, out IActionResult? error)
        {
            var contextoId = HttpContext.GetEstabelecimentoId();
            var isSuperAdmin = HttpContext.IsSuperAdmin();

            if (isSuperAdmin)
            {
                if (requestedId.HasValue && requestedId.Value != Guid.Empty)
                {
                    effectiveId = requestedId.Value;
                    error = null;
                    return true;
                }

                if (contextoId.HasValue)
                {
                    effectiveId = contextoId.Value;
                    error = null;
                    return true;
                }

                effectiveId = Guid.Empty;
                error = BadRequest(new { success = false, error = "Informe um estabelecimento valido." });
                return false;
            }

            if (!contextoId.HasValue)
            {
                effectiveId = Guid.Empty;
                error = BadRequest(new { success = false, error = "Selecione um estabelecimento para continuar." });
                return false;
            }

            if (requestedId.HasValue && requestedId.Value != Guid.Empty && requestedId.Value != contextoId.Value)
            {
                effectiveId = Guid.Empty;
                error = StatusCode(403, new { success = false, error = "Acesso negado ao estabelecimento informado." });
                return false;
            }

            effectiveId = contextoId.Value;
            error = null;
            return true;
        }
    }
}
