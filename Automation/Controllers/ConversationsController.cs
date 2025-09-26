// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
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
        public async Task<IActionResult> ListarConversas([FromQuery] string? estado, [FromQuery] int? responsavel, [FromQuery] bool incluirArquivadas = false)
        {
            var conversas = await _conversationRepository.ListarConversasAsync(estado, responsavel, incluirArquivadas);
            return Ok(conversas);
        }

        [HttpGet("{id:guid}/mensagens")]
        public async Task<IActionResult> ObterMensagens(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = DefaultPageSize)
        {
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

            var historico = await _conversationRepository.ObterHistoricoConversaAsync(id, page, pageSize);
            if (historico == null)
            {
                return NotFound();
            }

            return Ok(historico);
        }

        [HttpPost("{id:guid}/assign")]
        public async Task<IActionResult> AtribuirConversas(Guid id, [FromBody] AssignConversationRequest request)
        {
            if (request == null || request.IdAgente <= 0)
            {
                return BadRequest(new { error = "IdAgente deve ser informado." });
            }

            var atualizado = await _conversationRepository.AtribuirConversaAsync(id, request.IdAgente);
            if (!atualizado)
            {
                return NotFound();
            }

            var detalhes = await _conversationRepository.ObterDetalhesConversaAsync(id);
            return detalhes != null ? Ok(detalhes) : Ok();
        }

        [HttpPost("{id:guid}/close")]
        public async Task<IActionResult> FecharConversa(Guid id, [FromBody] CloseConversationRequest request)
        {
            var idAgente = request?.IdAgente > 0 ? request.IdAgente : null;
            var sucesso = await _conversationRepository.FecharConversaAsync(id, idAgente, request?.Motivo);
            if (!sucesso)
            {
                return NotFound();
            }

            var detalhes = await _conversationRepository.ObterDetalhesConversaAsync(id);
            return detalhes != null ? Ok(detalhes) : Ok();
        }

        [HttpPost("{id:guid}/archive")]
        public async Task<IActionResult> ArquivarConversa(Guid id)
        {
            var detalhes = await _conversationRepository.ArquivarConversaAsync(id);
            if (detalhes == null)
            {
                return NotFound();
            }

            return Ok(detalhes);
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

