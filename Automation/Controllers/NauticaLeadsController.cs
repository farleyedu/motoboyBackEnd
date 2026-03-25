using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Automation.Controllers
{
    [ApiController]
    [Route("nautica/leads")]
    public class NauticaLeadsController : ControllerBase
    {
        // Status canônicos + lojista_minimo aceito como alias legado temporário
        private static readonly HashSet<string> StatusLeadPermitidos = new(StringComparer.OrdinalIgnoreCase)
        {
            "incompleto",
            "consumidor_final",
            "lojista",
            "lojista_qualificado",
            "lojista_minimo" // alias legado — normalizado para lojista_qualificado antes de persistir
        };

        private readonly INauticaPainelRepository _repository;

        public NauticaLeadsController(INauticaPainelRepository repository)
        {
            _repository = repository;
        }

        [HttpGet]
        [RequirePermission("Leads", "visualizar")]
        public async Task<IActionResult> Listar(
            [FromQuery] string? busca,
            [FromQuery] string? status,
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

            var (itens, total) = await _repository.ListarLeadsAsync(estabelecimentoId, busca, status, paginaNormalizada, tamanhoNormalizado);
            var contagens = await _repository.ContarLeadsPorStatusAsync(estabelecimentoId, busca);
            var totaisPorStatus = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["incompleto"] = 0,
                ["consumidor_final"] = 0,
                ["lojista"] = 0,
                ["lojista_qualificado"] = 0
            };

            foreach (var count in contagens)
            {
                if (string.IsNullOrWhiteSpace(count.Status)) continue;

                // Agregar lojista_minimo legado em lojista_qualificado
                var chave = string.Equals(count.Status, "lojista_minimo", StringComparison.OrdinalIgnoreCase)
                    ? "lojista_qualificado"
                    : count.Status;

                if (totaisPorStatus.ContainsKey(chave))
                    totaisPorStatus[chave] += count.Total;
            }

            return Ok(new NauticaLeadListResponseDto
            {
                Pagina = paginaNormalizada,
                TamanhoPagina = tamanhoNormalizado,
                Total = total,
                TotaisPorStatus = totaisPorStatus,
                Itens = itens
            });
        }

        [HttpGet("{idLead:guid}")]
        [RequirePermission("Leads", "visualizar")]
        public async Task<IActionResult> ObterDetalhe(Guid idLead, [FromQuery] Guid? idEstabelecimento = null)
        {
            if (!TryResolveEstabelecimento(idEstabelecimento, out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var detalhe = await _repository.ObterLeadDetalheAsync(estabelecimentoId, idLead);
            return detalhe == null
                ? NotFound(new { success = false, error = "Lead nao encontrado." })
                : Ok(detalhe);
        }

        [HttpPatch("{idLead:guid}/status")]
        [RequirePermission("Leads", "mudar_status")]
        public async Task<IActionResult> AtualizarStatus(Guid idLead, [FromBody] UpdateNauticaLeadStatusRequest request, [FromQuery] Guid? idEstabelecimento = null)
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
                return NotFound(new { success = false, error = "Lead nao encontrado." });
            }

            var detalhe = await _repository.ObterLeadDetalheAsync(estabelecimentoId, idLead);
            return detalhe == null ? Ok() : Ok(detalhe);
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

            effectiveId = contextoId.Value;
            error = null;
            return true;
        }
    }
}
