// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
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

        [HttpGet("{idEstabelecimento}")]
        public async Task<IActionResult> Listar(Guid idEstabelecimento)
        {
            var itens = await _repo.ListaregrasAsync(idEstabelecimento);
            return Ok(itens);
        }

        public class CriarRegraRequest
        {
            public Guid IdEstabelecimento { get; set; }
            public string Contexto { get; set; } = string.Empty;
        }

        [HttpPost]
        public async Task<IActionResult> Criar([FromBody] CriarRegraRequest req)
        {
            if (req.IdEstabelecimento == Guid.Empty || string.IsNullOrWhiteSpace(req.Contexto))
                return BadRequest("idEstabelecimento e contexto são obrigatórios");
            var id = await _repo.CriarAsync(req.IdEstabelecimento, req.Contexto);
            return Ok(new { id });
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Excluir(Guid id)
        {
            if (id == Guid.Empty) return BadRequest("id inválido");
            var ok = await _repo.ExcluirAsync(id);
            return ok ? NoContent() : NotFound();
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

