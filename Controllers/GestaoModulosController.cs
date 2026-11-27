using System;
using System.Threading.Tasks;
using APIBack.DTOs.Common;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    /// <summary>Gestão (super admin): liga ou desliga um módulo só numa loja.</summary>
    [ApiController]
    public sealed class GestaoModulosController : GestaoControllerBase
    {
        private readonly IModulosDaLojaService _service;

        public GestaoModulosController(IModulosDaLojaService service) => _service = service;

        [HttpGet("api/gestao/estabelecimentos/{id:guid}/modulos")]
        public Task<IActionResult> Obter(Guid id) => Gestao(() => _service.ObterAsync(id, HttpContext.RequestAborted));

        [HttpPut("api/gestao/estabelecimentos/{id:guid}/modulos/{modulo}")]
        public Task<IActionResult> Alterar(Guid id, string modulo, [FromBody] AlterarModuloRequest? body) =>
            Gestao(() => _service.AlterarAsync(id, modulo, body ?? new AlterarModuloRequest(), CurrentUserId, HttpContext.RequestAborted));

        private async Task<IActionResult> Gestao(Func<Task<ModulosDaLojaDto>> acao)
        {
            if (!CurrentUserId.HasValue) return UnauthorizedResponse();
            if (!CurrentIsSuperAdmin) return ForbiddenResponse();
            Response.Headers.CacheControl = "no-store";
            try
            {
                return Ok(ApiResponse<ModulosDaLojaDto>.Ok(await acao()));
            }
            catch (ModuloDaLojaException ex)
            {
                return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo, ex.Detalhes));
            }
        }
    }
}
