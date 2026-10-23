using APIBack.DTOs.Common;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    /// <summary>Gestao central do dono da Zippy (/apps/gestao no admin): carteira, dinheiro, contas, fila e ficha. So super admin.</summary>
    [ApiController]
    [Route("api/gestao/central")]
    public sealed class GestaoCentralController(IGestaoCentralService service) : GestaoControllerBase
    {
        private async Task<IActionResult> Run<T>(Func<int, CancellationToken, Task<T>> action)
        {
            if (!CurrentUserId.HasValue) return UnauthorizedResponse();
            if (!CurrentIsSuperAdmin) return ForbiddenResponse();
            Response.Headers.CacheControl = "no-store";
            try
            {
                return Ok(ApiResponse<T>.Ok(await action(CurrentUserId.Value, HttpContext.RequestAborted)));
            }
            catch (GestaoCentralException ex)
            {
                return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message));
            }
        }

        private Task<IActionResult> Run(Func<int, CancellationToken, Task> action) =>
            Run<bool>(async (usuario, ct) => { await action(usuario, ct); return true; });

        private static DateOnly MesOuAtual(string? mes) =>
            DateOnly.TryParseExact($"{mes}-01", "yyyy-MM-dd", out var value) ? value : DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3));

        [HttpGet("carteira")]
        public Task<IActionResult> Carteira() => Run((_, ct) => service.CarteiraAsync(ct));

        [HttpGet("dinheiro")]
        public Task<IActionResult> Dinheiro([FromQuery] string? mes) => Run((_, ct) => service.DinheiroAsync(MesOuAtual(mes), ct));

        [HttpGet("contas")]
        public Task<IActionResult> Contas([FromQuery] string? mes) => Run((_, ct) => service.ContasAsync(MesOuAtual(mes), ct));

        [HttpPost("contas")]
        public Task<IActionResult> CriarConta([FromBody] SalvarContaRequest request) => Run((usuario, ct) => service.CriarContaAsync(request, usuario, ct));

        [HttpPut("contas/{id:long}")]
        public Task<IActionResult> AtualizarConta(long id, [FromBody] SalvarContaRequest request) =>
            Run((usuario, ct) => service.AtualizarContaAsync(id, request, usuario, ct));

        [HttpPost("contas/{id:long}/pagar")]
        public Task<IActionResult> PagarConta(long id, [FromBody] PagarContaRequest request) =>
            Run((usuario, ct) => service.PagarContaAsync(id, request, usuario, ct));

        [HttpDelete("contas/{id:long}")]
        public Task<IActionResult> CancelarConta(long id) => Run((usuario, ct) => service.CancelarContaAsync(id, usuario, ct));

        [HttpGet("responsaveis")]
        public Task<IActionResult> Responsaveis() => Run((_, ct) => service.ResponsaveisAsync(ct));

        [HttpGet("pendencias")]
        public Task<IActionResult> Pendencias() => Run((_, ct) => service.PendenciasEstadoAsync(ct));

        [HttpPost("pendencias/adiar")]
        public Task<IActionResult> Adiar([FromQuery] string chave, [FromBody] AdiarPendenciaRequest request) =>
            Run((usuario, ct) => service.AdiarAsync(chave, request.Ate, usuario, ct));

        [HttpPost("pendencias/dispensar")]
        public Task<IActionResult> Dispensar([FromQuery] string chave, [FromBody] DispensarPendenciaRequest request) =>
            Run((usuario, ct) => service.DispensarAsync(chave, request.Motivo ?? string.Empty, usuario, ct));

        [HttpDelete("pendencias")]
        public Task<IActionResult> Reabrir([FromQuery] string chave) => Run((_, ct) => service.ReabrirAsync(chave, ct));

        [HttpGet("lojas/{id:guid}/motoboys")]
        public Task<IActionResult> Motoboys(Guid id) => Run((_, ct) => service.MotoboysDaLojaAsync(id, ct));

        [HttpGet("lembretes")]
        public Task<IActionResult> Lembretes() => Run((_, ct) => service.LembretesAsync(ct));

        [HttpPost("lojas/{id:guid}/entrada")]
        public Task<IActionResult> RegistrarEntrada(Guid id) => Run((usuario, ct) => service.RegistrarEntradaAsync(id, usuario, ct));

        [HttpGet("acessos")]
        public Task<IActionResult> Acessos() => Run((_, ct) => service.UltimosAcessosAsync(ct));

        [HttpGet("lojas/{id:guid}/notas")]
        public Task<IActionResult> Notas(Guid id) => Run((_, ct) => service.NotasAsync(id, ct));

        [HttpPost("lojas/{id:guid}/notas")]
        public Task<IActionResult> CriarNota(Guid id, [FromBody] SalvarNotaRequest request) =>
            Run((usuario, ct) => service.CriarNotaAsync(id, request, usuario, ct));

        [HttpPost("lojas/{id:guid}/notas/{notaId:long}/concluir")]
        public Task<IActionResult> ConcluirNota(Guid id, long notaId) => Run((usuario, ct) => service.ConcluirNotaAsync(id, notaId, usuario, ct));

        [HttpGet("lojas/{id:guid}/linha-do-tempo")]
        public Task<IActionResult> LinhaDoTempo(Guid id) => Run((_, ct) => service.LinhaDoTempoAsync(id, ct));
    }
}
