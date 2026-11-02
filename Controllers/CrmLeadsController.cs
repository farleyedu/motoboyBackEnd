using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Acompanhamento.Relacionamento;
using APIBack.DTOs.Common;
using APIBack.DTOs.CRM;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Controllers
{
    /// <summary>
    /// Leads do CRM novo (so a equipe Zippy). Contrato proprio, independente de /api/crm/oportunidades.
    /// Erros: 401 sem login, 403 sem permissao, 409 DUPLICADO (details.candidatos; repita com decisaoDuplicidade="criar_novo" depois de conferir)
    /// ou CHAVE_REUTILIZADA, 422 PROXIMO_PASSO_OBRIGATORIO / VALIDACAO (details por campo).
    /// </summary>
    [ApiController]
    [Route("api/crm/leads")]
    public sealed class CrmLeadsController : CrmControllerBase
    {
        private readonly ICrmService _service;
        private readonly ILogger<CrmLeadsController> _logger;

        public CrmLeadsController(ICrmService service, ILogger<CrmLeadsController> logger)
        {
            _service = service;
            _logger = logger;
        }

        /// <summary>Leads e lojas com o mesmo telefone ou nome parecido (sem acento). Nunca une nada: so mostra.</summary>
        [HttpGet("parecidos")]
        public Task<IActionResult> Parecidos([FromQuery] string? nome, [FromQuery] string? telefone) =>
            Executar(async ct => Ok(ApiResponse<IReadOnlyList<CrmLeadCandidatoDto>>.Ok(await _service.ListarLeadsParecidosAsync(nome, telefone, ct))));

        [HttpPost]
        public Task<IActionResult> Criar([FromBody] CriarCrmLeadRequest? request) => Executar(async ct =>
        {
            if (request == null) throw new RelacionamentoException(422, "VALIDACAO", "Envie os dados do lead.");
            var chave = Request.Headers.TryGetValue("Idempotency-Key", out var valor) ? valor.ToString() : null;
            var lead = await _service.CriarLeadAsync(CurrentUserId!.Value, request, chave, ct);
            return lead.Repetida
                ? Ok(ApiResponse<CrmLeadCriadoDto>.Ok(lead))
                : StatusCode(StatusCodes.Status201Created, ApiResponse<CrmLeadCriadoDto>.Ok(lead));
        });

        private async Task<IActionResult> Executar(Func<System.Threading.CancellationToken, Task<IActionResult>> acao)
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;
            Response.Headers.CacheControl = "no-store";
            try
            {
                return await acao(HttpContext.RequestAborted);
            }
            catch (RelacionamentoException ex)
            {
                return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
            {
                _logger.LogWarning("[crm.leads] ev=migracao_pendente objeto={O}", ex.TableName ?? ex.ColumnName);
                return StatusCode(StatusCodes.Status422UnprocessableEntity,
                    ApiResponse<object>.Fail("Faltam migracoes do CRM neste banco.", "MIGRACAO_PENDENTE"));
            }
            catch (OperationCanceledException)
            {
                return StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[crm.leads] ev=erro");
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao processar o lead.", "INTERNAL_ERROR"));
            }
        }
    }
}
