using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Services;
using APIBack.Extensions;
using Microsoft.AspNetCore.Http;
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
        private readonly GarageSimulationStorageService _storageService;

        public GaragemLeadsController(IGaragemPainelRepository repository, GarageSimulationStorageService storageService)
        {
            _repository = repository;
            _storageService = storageService;
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

            var (itens, total) = await _repository.ListarLeadsAsync(estabelecimentoId, busca, status, objetivo, paginaNormalizada, tamanhoNormalizado);
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

            return Ok(new GarageLeadListResponseDto
            {
                Pagina = paginaNormalizada,
                TamanhoPagina = tamanhoNormalizado,
                Total = total,
                TotaisPorStatus = totaisPorStatus,
                Itens = itens
            });
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
            return detalhe == null ? NotFound(new { success = false, error = "Lead nao encontrado." }) : Ok(detalhe);
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
                return NotFound(new { success = false, error = "Lead nao encontrado." });
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

            if (!string.IsNullOrWhiteSpace(request.Status) && !StatusSimulacaoPermitidos.Contains(request.Status.Trim().ToLowerInvariant()))
            {
                return BadRequest(new { success = false, error = "Status de simulacao invalido." });
            }

            request.CriadoPorUsuarioId = HttpContext.GetUserId();
            var simulacao = await _repository.CriarSimulacaoAsync(estabelecimentoId, idLead, request);
            return simulacao == null
                ? NotFound(new { success = false, error = "Lead nao encontrado para o estabelecimento informado." })
                : StatusCode(201, simulacao);
        }

        [HttpPatch("{idLead:guid}/simulacoes/{idSimulacao:guid}")]
        [HttpPut("{idLead:guid}/simulacoes/{idSimulacao:guid}")]
        [RequirePermission("Garagem", "editar")]
        public async Task<IActionResult> AtualizarSimulacao(Guid idLead, Guid idSimulacao, [FromBody] UpdateGarageLeadSimulationRequest request, [FromQuery] Guid? idEstabelecimento = null)
        {
            if (!TryResolveEstabelecimento(idEstabelecimento, out var estabelecimentoId, out var error))
            {
                return error!;
            }

            if (request == null)
            {
                return BadRequest(new { success = false, error = "Corpo da requisicao e obrigatorio." });
            }

            if (!string.IsNullOrWhiteSpace(request.Status) && !StatusSimulacaoPermitidos.Contains(request.Status.Trim().ToLowerInvariant()))
            {
                return BadRequest(new { success = false, error = "Status de simulacao invalido." });
            }

            var simulacao = await _repository.AtualizarSimulacaoAsync(estabelecimentoId, idLead, idSimulacao, request);
            return simulacao == null ? NotFound(new { success = false, error = "Simulacao nao encontrada." }) : Ok(simulacao);
        }

        [HttpPost("{idLead:guid}/simulacoes/{idSimulacao:guid}/arquivos")]
        [RequirePermission("Garagem", "editar")]
        public async Task<IActionResult> UploadArquivo(Guid idLead, Guid idSimulacao, [FromForm] IFormFile file, [FromQuery] Guid? idEstabelecimento = null)
        {
            if (!TryResolveEstabelecimento(idEstabelecimento, out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var simulacao = await _repository.ObterSimulacaoAsync(estabelecimentoId, idLead, idSimulacao);
            if (simulacao == null)
            {
                return NotFound(new { success = false, error = "Simulacao nao encontrada." });
            }

            try
            {
                var saved = await _storageService.SaveAsync(idSimulacao, file, HttpContext.RequestAborted);
                var persisted = await _repository.AdicionarArquivoSimulacaoAsync(estabelecimentoId, idLead, idSimulacao, new GarageLeadSimulationFileDto
                {
                    Id = saved.Id,
                    Nome = saved.Nome,
                    Tamanho = saved.Tamanho,
                    ContentType = saved.ContentType,
                    CaminhoRelativo = saved.CaminhoRelativo,
                    Url = saved.Url,
                    Data = saved.Data
                });

                if (persisted == null)
                {
                    await _storageService.DeleteAsync(saved.CaminhoRelativo);
                    return NotFound(new { success = false, error = "Nao foi possivel vincular o arquivo a simulacao." });
                }

                return Ok(saved);
            }
            catch (ConversationManagementException ex)
            {
                return StatusCode(ex.StatusCode, new { success = false, error = ex.Message });
            }
        }

        [HttpDelete("{idLead:guid}/simulacoes/{idSimulacao:guid}/arquivos/{idArquivo:guid}")]
        [RequirePermission("Garagem", "deletar")]
        public async Task<IActionResult> RemoverArquivo(Guid idLead, Guid idSimulacao, Guid idArquivo, [FromQuery] Guid? idEstabelecimento = null)
        {
            if (!TryResolveEstabelecimento(idEstabelecimento, out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var simulacao = await _repository.ObterSimulacaoAsync(estabelecimentoId, idLead, idSimulacao);
            if (simulacao == null)
            {
                return NotFound(new { success = false, error = "Simulacao nao encontrada." });
            }

            var arquivo = simulacao.Arquivos.FirstOrDefault(a => a.Id == idArquivo);
            if (arquivo == null)
            {
                return NotFound(new { success = false, error = "Arquivo nao encontrado." });
            }

            var removido = await _repository.RemoverArquivoSimulacaoAsync(estabelecimentoId, idLead, idSimulacao, idArquivo);
            if (!removido)
            {
                return NotFound(new { success = false, error = "Arquivo nao encontrado." });
            }

            await _storageService.DeleteAsync(arquivo.CaminhoRelativo);
            return NoContent();
        }

        [HttpDelete("{idLead:guid}/simulacoes/{idSimulacao:guid}")]
        [RequirePermission("Garagem", "deletar")]
        public async Task<IActionResult> RemoverSimulacao(Guid idLead, Guid idSimulacao, [FromQuery] Guid? idEstabelecimento = null)
        {
            if (!TryResolveEstabelecimento(idEstabelecimento, out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var simulacao = await _repository.ObterSimulacaoAsync(estabelecimentoId, idLead, idSimulacao);
            if (simulacao == null)
            {
                return NotFound(new { success = false, error = "Simulacao nao encontrada." });
            }

            var removido = await _repository.RemoverSimulacaoAsync(estabelecimentoId, idLead, idSimulacao);
            if (!removido)
            {
                return NotFound(new { success = false, error = "Simulacao nao encontrada." });
            }

            foreach (var arquivo in simulacao.Arquivos)
            {
                await _storageService.DeleteAsync(arquivo.CaminhoRelativo);
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
