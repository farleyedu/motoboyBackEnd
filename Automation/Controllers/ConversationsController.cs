// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Automation.Controllers
{
    [ApiController]
    [Route("conversas")]
    public class ConversationsController : ControllerBase
    {
        private const int DefaultPageSize = 50;
        private const int MaxPageSize = 200;
        private readonly IConversationRepository _conversationRepository;

        public ConversationsController(IConversationRepository conversationRepository)
        {
            _conversationRepository = conversationRepository;
        }

        [HttpGet]
        [RequirePermission("WhatsApp", "visualizar")]
        public async Task<IActionResult> ListarConversas([FromQuery] string? estado, [FromQuery] int? responsavel, [FromQuery] bool incluirArquivadas = false)
        {
            var estabelecimentoId = HttpContext.GetEstabelecimentoId();
            if (!estabelecimentoId.HasValue)
            {
                return BadRequest(new { success = false, error = "Selecione um estabelecimento para listar conversas." });
            }

            var conversas = await _conversationRepository.ListarConversasAsync(estado, responsavel, incluirArquivadas, estabelecimentoId.Value);
            return Ok(conversas);
        }

        [HttpGet("{id:guid}/mensagens")]
        [RequirePermission("WhatsApp", "visualizar")]
        public async Task<IActionResult> ObterMensagens(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = DefaultPageSize)
        {
            var estabelecimentoId = HttpContext.GetEstabelecimentoId();
            if (!estabelecimentoId.HasValue)
            {
                return BadRequest(new { success = false, error = "Selecione um estabelecimento para consultar mensagens." });
            }

            if (page < 1)
            {
                page = 1;
            }

            if (pageSize <= 0)
            {
                pageSize = DefaultPageSize;
            }

            if (pageSize > MaxPageSize)
            {
                pageSize = MaxPageSize;
            }

            var historico = await _conversationRepository.ObterHistoricoConversaAsync(id, page, pageSize, estabelecimentoId.Value);
            if (historico == null)
            {
                return NotFound();
            }

            return Ok(historico);
        }

        [HttpPost("{id:guid}/assign")]
        [RequirePermission("WhatsApp", "editar")]
        public async Task<IActionResult> AtribuirConversas(Guid id, [FromBody] AssignConversationRequest request)
        {
            var estabelecimentoId = HttpContext.GetEstabelecimentoId();
            if (!estabelecimentoId.HasValue)
            {
                return BadRequest(new { success = false, error = "Selecione um estabelecimento para atribuir conversas." });
            }

            if (request == null || request.IdAgente <= 0)
            {
                return BadRequest(new { error = "IdAgente deve ser informado." });
            }

            var atualizado = await _conversationRepository.AtribuirConversaAsync(id, request.IdAgente, estabelecimentoId.Value);
            if (!atualizado)
            {
                return NotFound();
            }

            var detalhes = await _conversationRepository.ObterDetalhesConversaAsync(id, estabelecimentoId.Value);
            return detalhes != null ? Ok(detalhes) : Ok();
        }

        [HttpPost("{id:guid}/close")]
        [RequirePermission("WhatsApp", "editar")]
        public async Task<IActionResult> FecharConversa(Guid id, [FromBody] CloseConversationRequest request)
        {
            var estabelecimentoId = HttpContext.GetEstabelecimentoId();
            if (!estabelecimentoId.HasValue)
            {
                return BadRequest(new { success = false, error = "Selecione um estabelecimento para fechar conversas." });
            }

            var idAgente = request?.IdAgente > 0 ? request.IdAgente : null;
            var sucesso = await _conversationRepository.FecharConversaAsync(id, idAgente, request?.Motivo, estabelecimentoId.Value);
            if (!sucesso)
            {
                return NotFound();
            }

            var detalhes = await _conversationRepository.ObterDetalhesConversaAsync(id, estabelecimentoId.Value);
            return detalhes != null ? Ok(detalhes) : Ok();
        }

        [HttpPost("{id:guid}/archive")]
        [RequirePermission("WhatsApp", "editar")]
        public async Task<IActionResult> ArquivarConversa(Guid id)
        {
            var estabelecimentoId = HttpContext.GetEstabelecimentoId();
            if (!estabelecimentoId.HasValue)
            {
                return BadRequest(new { success = false, error = "Selecione um estabelecimento para arquivar conversas." });
            }

            var detalhes = await _conversationRepository.ArquivarConversaAsync(id, estabelecimentoId.Value);
            if (detalhes == null)
            {
                return NotFound();
            }

            return Ok(detalhes);
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

