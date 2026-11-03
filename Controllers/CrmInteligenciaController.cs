using System;
using System.Threading;
using System.Threading.Tasks;
using APIBack.AssistenteComercial.Inteligencia;
using APIBack.DTOs.Common;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    /// <summary>
    /// Objecoes, recomendacoes e coaching do CRM. Nada aqui promete funcionalidade, desconto ou probabilidade: tudo vem de fatos registrados e do catalogo real.
    /// Erros: 401 sem login, 403 fora da equipe do CRM ou oportunidade de outro responsavel (super admin enxerga todas), 404 oportunidade/objecao/recomendacao
    /// inexistente, 409 versao desatualizada ou transicao invalida, 422 dado invalido (evidencia ausente, hipotese impeditiva...), 503 catalogo comercial indisponivel.
    /// </summary>
    [ApiController]
    [Route("api/crm")]
    public sealed class CrmInteligenciaController : CrmControllerBase
    {
        private readonly IInteligenciaService _service;

        public CrmInteligenciaController(IInteligenciaService service) => _service = service;

        /// <summary>Lista as objecoes; antes, traz as de audio ja confirmadas pela pessoa (idempotente).</summary>
        [HttpGet("oportunidades/{id:guid}/objecoes")]
        public Task<IActionResult> Objecoes(Guid id) => Executar((u, ct) => _service.ListarObjecoesAsync(u, CurrentIsSuperAdmin, id, ct));

        /// <summary>Sem <c>id</c> no corpo cria; com <c>id</c> corrige (exige motivoCorrecao e versaoEsperada). Tudo vai para a auditoria.</summary>
        [HttpPost("oportunidades/{id:guid}/objecoes")]
        public Task<IActionResult> RegistrarObjecao(Guid id, [FromBody] RegistrarObjecaoRequest request) =>
            Executar((u, ct) => _service.RegistrarObjecaoAsync(u, CurrentIsSuperAdmin, id, request, ct));

        /// <summary>Recomendacoes por prioridade. Gera as que faltam (idempotente: a mesma situacao nao duplica a sugestao).</summary>
        [HttpGet("oportunidades/{id:guid}/recomendacoes")]
        public Task<IActionResult> Recomendacoes(Guid id) => Executar((u, ct) => _service.RecomendacoesAsync(u, CurrentIsSuperAdmin, id, ct));

        [HttpPost("oportunidades/{id:guid}/recomendacoes/{recomendacaoId:guid}/feedback")]
        public Task<IActionResult> Feedback(Guid id, Guid recomendacaoId, [FromBody] FeedbackRequest request) =>
            Executar((u, ct) => _service.FeedbackAsync(u, CurrentIsSuperAdmin, id, recomendacaoId, request, ct));

        /// <summary>Padroes do proprio responsavel; <c>responsavelId</c> de outra pessoa so para super admin.</summary>
        [HttpGet("coaching")]
        public Task<IActionResult> Coaching([FromQuery] int? responsavelId) =>
            Executar((u, ct) => _service.CoachingAsync(u, CurrentIsSuperAdmin, responsavelId, ct));

        private async Task<IActionResult> Executar<T>(Func<int, CancellationToken, Task<T>> operacao)
        {
            if (!CurrentUserId.HasValue) return UnauthorizedResponse();
            Response.Headers.CacheControl = "no-store";
            try
            {
                return Ok(ApiResponse<T>.Ok(await operacao(CurrentUserId.Value, HttpContext.RequestAborted)));
            }
            catch (InteligenciaException ex)
            {
                return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo, ex.Detalhes));
            }
        }
    }
}
