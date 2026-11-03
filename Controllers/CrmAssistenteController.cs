using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using APIBack.AssistenteComercial.Audio;
using APIBack.DTOs.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    /// <summary>
    /// Assistente de visitas por audio. Toda entrada e uma PROPOSTA: nada muda no CRM ate a pessoa confirmar, e so as alteracoes que ela aprovar.
    /// Erros: 401 sem login, 403 sem funcionario verificado (WhatsApp) ou oportunidade de outro responsavel, 404 entrada de outra pessoa,
    /// 409 ja processado / oportunidade alterada / chave reutilizada, 422 ambiguo ou incompleto (details.candidatos), 503 IA indisponivel
    /// ou teto de custo (details traz o campo de configuracao que falta). Confirmar aceita o cabecalho Idempotency-Key.
    /// </summary>
    [ApiController]
    [Route("api/crm/assistente")]
    public sealed class CrmAssistenteController : CrmControllerBase
    {
        private readonly IAudioService _service;

        public CrmAssistenteController(IAudioService service) => _service = service;

        [HttpGet("entradas")]
        public Task<IActionResult> Listar([FromQuery] string? estado, [FromQuery] int limite = 30) =>
            Executar((u, ct) => _service.ListarAsync(u, estado, limite, ct));

        [HttpGet("entradas/{id:guid}")]
        public Task<IActionResult> Obter(Guid id) => Executar((u, ct) => _service.ObterAsync(u, id, ct));

        [HttpPost("entradas/{id:guid}/confirmar")]
        public Task<IActionResult> Confirmar(Guid id, [FromBody] ConfirmarRequest? request)
        {
            var chave = Request.Headers.TryGetValue("Idempotency-Key", out var valor) ? valor.ToString() : null;
            return Executar((u, ct) => _service.ConfirmarAsync(u, id, request, chave, ct));
        }

        [HttpPost("entradas/{id:guid}/rejeitar")]
        public Task<IActionResult> Rejeitar(Guid id, [FromBody] RejeitarRequest? request) =>
            Executar((u, ct) => _service.RejeitarAsync(u, id, request?.Motivo, ct));

        [HttpPost("entradas/{id:guid}/reprocessar")]
        public Task<IActionResult> Reprocessar(Guid id) => Executar(async (u, ct) => await _service.ReprocessarAsync(u, id, ct));

        /// <summary>Historico recente da oportunidade, so se o usuario e o responsavel (pre-visita).</summary>
        [HttpGet("oportunidades/{id:guid}/pre-visita")]
        public Task<IActionResult> PreVisita(Guid id) => Executar((u, ct) => _service.PreVisitaAsync(u, id, ct));

        [HttpGet("consentimento")]
        public Task<IActionResult> Consentimento() => Executar(async (u, ct) => new { consentido = await _service.ConsentimentoAsync(u, ct), versao = AudioService.VersaoTextoConsentimento });

        [HttpPost("consentimento")]
        public Task<IActionResult> Consentir() => Executar(async (u, ct) => { await _service.ConsentirAsync(u, ct); return new { consentido = true, versao = AudioService.VersaoTextoConsentimento }; });

        [HttpDelete("consentimento")]
        public Task<IActionResult> Revogar() => Executar(async (u, ct) => { await _service.RevogarConsentimentoAsync(u, ct); return new { consentido = false }; });

        private async Task<IActionResult> Executar<T>(Func<int, CancellationToken, Task<T>> operacao)
        {
            if (!CurrentUserId.HasValue) return UnauthorizedResponse();
            Response.Headers.CacheControl = "no-store";
            try
            {
                return Ok(ApiResponse<T>.Ok(await operacao(CurrentUserId.Value, HttpContext.RequestAborted)));
            }
            catch (AssistenteAudioException ex)
            {
                return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo, ex.Detalhes));
            }
        }
    }
}
