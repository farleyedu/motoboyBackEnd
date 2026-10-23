using APIBack.DTOs.Common;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    /// <summary>Chamados: a loja do token abre e conversa em api/v2/chamados; a Gestao (super admin) atende em api/gestao/chamados.</summary>
    [ApiController]
    public sealed class ChamadosController(IChamadosService service) : GestaoControllerBase
    {
        private async Task<IActionResult> Run<T>(bool gestao, Func<int, Guid?, CancellationToken, Task<T>> action)
        {
            if (!CurrentUserId.HasValue) return UnauthorizedResponse();
            if (gestao && !CurrentIsSuperAdmin) return ForbiddenResponse();
            var loja = gestao ? null : CurrentEstabelecimentoId;
            if (!gestao && !loja.HasValue) return BadRequest(ApiResponse<object>.Fail("Selecione a loja."));
            Response.Headers.CacheControl = "no-store";
            try { return Ok(ApiResponse<T>.Ok(await action(CurrentUserId.Value, loja, HttpContext.RequestAborted))); }
            catch (ChamadoException ex) { return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message)); }
        }

        [HttpGet("api/v2/chamados")]
        public Task<IActionResult> MeusChamados() => Run(false, (_, loja, ct) => service.ListarAsync(loja, ct));

        [HttpPost("api/v2/chamados")]
        public Task<IActionResult> Abrir([FromBody] AbrirChamadoRequest request) => Run(false, (u, loja, ct) => service.AbrirAsync(loja!.Value, u, request, ct));

        [HttpGet("api/v2/chamados/{id:long}")]
        public Task<IActionResult> MeuChamado(long id) => Run(false, async (_, loja, ct) => { var (c, m) = await service.ObterAsync(id, loja, ct); return new { chamado = c, mensagens = m }; });

        [HttpPost("api/v2/chamados/{id:long}/mensagens")]
        public Task<IActionResult> LojaResponde(long id, [FromBody] ResponderChamadoRequest request) => Run(false, async (u, loja, ct) => { await service.ResponderAsync(id, loja, u, false, request.Texto ?? string.Empty, ct); return true; });

        [HttpPost("api/v2/chamados/{id:long}/nota")]
        public Task<IActionResult> Avaliar(long id, [FromBody] NotaChamadoRequest request) => Run(false, async (_, loja, ct) => { await service.AvaliarAsync(id, loja!.Value, request.Nota, ct); return true; });

        [HttpGet("api/gestao/chamados")]
        public Task<IActionResult> Todos() => Run(true, (_, _, ct) => service.ListarAsync(null, ct));

        [HttpGet("api/gestao/chamados/{id:long}")]
        public Task<IActionResult> Um(long id) => Run(true, async (_, _, ct) => { var (c, m) = await service.ObterAsync(id, null, ct); return new { chamado = c, mensagens = m }; });

        [HttpPost("api/gestao/chamados/{id:long}/mensagens")]
        public Task<IActionResult> ZippyResponde(long id, [FromBody] ResponderChamadoRequest request) => Run(true, async (u, _, ct) => { await service.ResponderAsync(id, null, u, true, request.Texto ?? string.Empty, ct); return true; });

        [HttpPost("api/gestao/chamados/{id:long}/status")]
        public Task<IActionResult> Status(long id, [FromBody] StatusChamadoRequest request) => Run(true, async (_, _, ct) => { await service.MudarStatusAsync(id, request.Status ?? string.Empty, ct); return true; });
    }
}
