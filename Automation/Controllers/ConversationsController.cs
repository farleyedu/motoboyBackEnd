// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Services;
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
        private readonly ConversationManagementService _managementService;

        public ConversationsController(
            IConversationRepository conversationRepository,
            ConversationManagementService managementService)
        {
            _conversationRepository = conversationRepository;
            _managementService = managementService;
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

        [HttpGet("agentes")]
        [RequirePermission("WhatsApp", "visualizar")]
        public async Task<IActionResult> ListarAgentes()
        {
            var estabelecimentoId = HttpContext.GetEstabelecimentoId();
            if (!estabelecimentoId.HasValue)
            {
                return BadRequest(new { success = false, error = "Selecione um estabelecimento para listar agentes." });
            }

            var agentes = await _managementService.ListarAgentesAsync(estabelecimentoId.Value);
            return Ok(agentes);
        }

        [HttpGet("{id:guid}/mensagens")]
        [RequirePermission("WhatsApp", "visualizar")]
        public async Task<IActionResult> ObterMensagens(Guid id, [FromQuery] DateTime? before = null, [FromQuery] int pageSize = DefaultPageSize)
        {
            var estabelecimentoId = HttpContext.GetEstabelecimentoId();
            if (!estabelecimentoId.HasValue)
            {
                return BadRequest(new { success = false, error = "Selecione um estabelecimento para consultar mensagens." });
            }

            pageSize = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);

            // Garante UTC — ASP.NET pode parsear o query param como Unspecified dependendo do formato
            var beforeUtc = before.HasValue
                ? DateTime.SpecifyKind(before.Value, DateTimeKind.Utc)
                : (DateTime?)null;

            var historico = await _conversationRepository.ObterHistoricoConversaAsync(id, beforeUtc, pageSize, estabelecimentoId.Value);
            return historico == null ? NotFound(new { success = false, error = "Conversa nao encontrada." }) : Ok(historico);
        }

        [HttpPost("{id:guid}/assign")]
        [RequirePermission("WhatsApp", "assumir_conversa")]
        public async Task<IActionResult> AtribuirConversa(Guid id, [FromBody] AssignConversationRequest request)
            => await ExecuteManagementAsync(() => _managementService.AssignAsync(id, RequireEstabelecimento(), HttpContext.GetUserId(), HttpContext.GetUserNome(), request));

        [HttpPost("{id:guid}/back-to-bot")]
        [RequirePermission("WhatsApp", "devolver_robo")]
        public async Task<IActionResult> VoltarParaBot(Guid id)
            => await ExecuteManagementAsync(() => _managementService.BackToBotAsync(id, RequireEstabelecimento(), HttpContext.GetUserId(), HttpContext.GetUserNome()));

        [HttpPatch("{id:guid}/status")]
        [RequirePermission("WhatsApp", "assumir_conversa")]
        public async Task<IActionResult> AtualizarStatus(Guid id, [FromBody] UpdateConversationStatusRequest request)
            => await ExecuteManagementAsync(() => _managementService.UpdateStatusAsync(id, RequireEstabelecimento(), HttpContext.GetUserId(), HttpContext.GetUserNome(), request));

        [HttpPost("{id:guid}/close")]
        [RequirePermission("WhatsApp", "encerrar")]
        public async Task<IActionResult> FecharConversa(Guid id, [FromBody] CloseConversationRequest request)
            => await ExecuteManagementAsync(() => _managementService.CloseAsync(id, RequireEstabelecimento(), HttpContext.GetUserId(), HttpContext.GetUserNome(), request));

        [HttpPost("{id:guid}/reopen")]
        [RequirePermission("WhatsApp", "reabrir")]
        public async Task<IActionResult> ReabrirConversa(Guid id, [FromBody] ReopenConversationRequest request)
            => await ExecuteManagementAsync(() => _managementService.ReopenAsync(id, RequireEstabelecimento(), HttpContext.GetUserId(), HttpContext.GetUserNome(), request));

        [HttpPost("{id:guid}/read")]
        [RequirePermission("WhatsApp", "visualizar")]
        public async Task<IActionResult> MarcarComoLida(Guid id)
            => await ExecuteManagementAsync(() => _managementService.MarkAsReadAsync(id, RequireEstabelecimento()));

        [HttpPost("{id:guid}/mensagens")]
        [RequirePermission("WhatsApp", "enviar_mensagem")]
        public async Task<IActionResult> EnviarMensagem(Guid id, [FromBody] SendConversationMessageRequest request)
            => await ExecuteManagementAsync(() => _managementService.SendMessageAsync(id, RequireEstabelecimento(), HttpContext.GetUserId(), HttpContext.GetUserNome(), request));

        [HttpPost("{id:guid}/archive")]
        [RequirePermission("WhatsApp", "arquivar")]
        public async Task<IActionResult> ArquivarConversa(Guid id)
        {
            var estabelecimentoId = RequireEstabelecimento();
            var detalhes = await _conversationRepository.ArquivarConversaAsync(id, estabelecimentoId);
            return detalhes == null ? NotFound(new { success = false, error = "Conversa nao encontrada." }) : Ok(detalhes);
        }

        private Guid RequireEstabelecimento()
        {
            var estabelecimentoId = HttpContext.GetEstabelecimentoId();
            if (!estabelecimentoId.HasValue)
            {
                throw new ConversationManagementException(422, "Selecione um estabelecimento para continuar.");
            }

            return estabelecimentoId.Value;
        }

        private async Task<IActionResult> ExecuteManagementAsync(Func<Task<ConversationActionResponseDto>> action)
        {
            try
            {
                return Ok(await action());
            }
            catch (ConversationManagementException ex)
            {
                return StatusCode(ex.StatusCode, new { success = false, error = ex.Message, code = ex.Code });
            }
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
