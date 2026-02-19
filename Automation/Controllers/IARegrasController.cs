// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.Automation.Interfaces;
using APIBack.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Automation.Controllers
{
    [ApiController]
    [Route("api/ia/regras")]
    public class IARegrasController : ControllerBase
    {
        private readonly IIARegraRepository _repo;

        public IARegrasController(IIARegraRepository repo)
        {
            _repo = repo;
        }

        [HttpGet("{idEstabelecimento:guid}")]
        [RequirePermission("Configuracoes", "visualizar")]
        public async Task<IActionResult> Listar(Guid idEstabelecimento)
        {
            if (!TryResolveEstabelecimento(idEstabelecimento, out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var itens = await _repo.ListaregrasAsync(estabelecimentoId);
            return Ok(itens);
        }

        public class CriarRegraRequest
        {
            public Guid IdEstabelecimento { get; set; }
            public string Contexto { get; set; } = string.Empty;
        }

        [HttpPost]
        [RequirePermission("Configuracoes", "configurar")]
        public async Task<IActionResult> Criar([FromBody] CriarRegraRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Contexto))
            {
                return BadRequest(new { success = false, error = "idEstabelecimento e contexto sao obrigatorios." });
            }

            if (!TryResolveEstabelecimento(req.IdEstabelecimento, out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var id = await _repo.CriarAsync(estabelecimentoId, req.Contexto);
            return Ok(new { id });
        }

        [HttpDelete("{id:guid}")]
        [RequirePermission("Configuracoes", "deletar")]
        public async Task<IActionResult> Excluir(Guid id)
        {
            if (id == Guid.Empty)
            {
                return BadRequest(new { success = false, error = "id invalido." });
            }

            var estabelecimentoId = HttpContext.GetEstabelecimentoId();
            var isSuperAdmin = HttpContext.IsSuperAdmin();

            if (!isSuperAdmin && !estabelecimentoId.HasValue)
            {
                return BadRequest(new { success = false, error = "Selecione um estabelecimento para excluir regras." });
            }

            var ok = await _repo.ExcluirAsync(id, isSuperAdmin ? estabelecimentoId : estabelecimentoId!.Value);
            return ok ? NoContent() : NotFound();
        }

        private bool TryResolveEstabelecimento(Guid requestedId, out Guid effectiveId, out IActionResult? error)
        {
            var contextoId = HttpContext.GetEstabelecimentoId();
            var isSuperAdmin = HttpContext.IsSuperAdmin();

            if (isSuperAdmin)
            {
                if (requestedId != Guid.Empty)
                {
                    effectiveId = requestedId;
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

            if (requestedId == Guid.Empty)
            {
                effectiveId = contextoId.Value;
                error = null;
                return true;
            }

            if (requestedId != contextoId.Value)
            {
                effectiveId = Guid.Empty;
                error = StatusCode(403, new { success = false, error = "Acesso negado ao estabelecimento informado." });
                return false;
            }

            effectiveId = requestedId;
            error = null;
            return true;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
