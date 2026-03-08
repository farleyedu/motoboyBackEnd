// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Services;
using APIBack.Automation.Validators;
using APIBack.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Controllers
{
    [ApiController]
    [Route("automation")]
    public class AutomationController : ControllerBase
    {
        private readonly ILogger<AutomationController> _logger;
        private readonly ConversationService _conversationService;
        private readonly HandoverService _handoverService;
        private readonly ConversationManagementService _managementService;
        private readonly IConversationRepository _conversationRepository;
        private readonly AgenteService _agenteService;
        private readonly AgentReplyRequestValidator _validator = new();

        public AutomationController(
            ILogger<AutomationController> logger,
            ConversationService conversationService,
            HandoverService handoverService,
            ConversationManagementService managementService,
            IConversationRepository conversationRepository,
            AgenteService agenteService)
        {
            _logger = logger;
            _conversationService = conversationService;
            _handoverService = handoverService;
            _managementService = managementService;
            _conversationRepository = conversationRepository;
            _agenteService = agenteService;
        }

        [HttpPost("conversation/{idConversa:guid}/handover")]
        [RequirePermission("WhatsApp", "editar")]
        public async Task<IActionResult> EncaminharParaHumano(Guid idConversa, [FromBody] HandoverRequest req)
        {
            try
            {
                var estabelecimentoId = RequireEstabelecimento();
                var resposta = await _managementService.AssignAsync(
                    idConversa,
                    estabelecimentoId,
                    HttpContext.GetUserId(),
                    HttpContext.GetUserNome(),
                    new AssignConversationRequest
                    {
                        IdAgente = req?.Agente?.Id ?? 0,
                        NomeAgente = req?.Agente?.Nome
                    });

                HandoverAgentDto? agente = null;
                if (resposta.Controle?.AssignedAgentId is int idAgente && idAgente > 0)
                {
                    agente = await _agenteService.ObterAgentePorIdAsync(idAgente);
                }

                await _handoverService.DefinirHumanoAsync(
                    resposta.Controle?.ConversationId ?? idConversa,
                    agente ?? req?.Agente,
                    req?.ReservaConfirmada ?? false,
                    req?.Detalhes);

                return Ok(resposta);
            }
            catch (ConversationManagementException ex)
            {
                return StatusCode(ex.StatusCode, new { success = false, error = ex.Message });
            }
        }

        [HttpPost("conversation/{idConversa:guid}/back-to-bot")]
        [RequirePermission("WhatsApp", "editar")]
        public async Task<IActionResult> VoltarParaBot(Guid idConversa)
        {
            try
            {
                var resposta = await _managementService.BackToBotAsync(
                    idConversa,
                    RequireEstabelecimento(),
                    HttpContext.GetUserId(),
                    HttpContext.GetUserNome());

                return Ok(resposta);
            }
            catch (ConversationManagementException ex)
            {
                return StatusCode(ex.StatusCode, new { success = false, error = ex.Message });
            }
        }

        [HttpPost("agent/reply")]
        [RequirePermission("WhatsApp", "criar")]
        public async Task<IActionResult> RespostaAgente([FromBody] AgentReplyRequest req)
        {
            var (valido, erro) = _validator.Validar(req);
            if (!valido)
            {
                return BadRequest(new { erro });
            }

            try
            {
                var resposta = await _managementService.SendMessageAsync(
                    req.IdConversa,
                    RequireEstabelecimento(),
                    HttpContext.GetUserId(),
                    HttpContext.GetUserNome(),
                    new SendConversationMessageRequest { Mensagem = req.Mensagem });

                return Ok((object?)resposta.Mensagem ?? new { });
            }
            catch (ConversationManagementException ex)
            {
                return StatusCode(ex.StatusCode, new { erro = ex.Message });
            }
        }

        [HttpGet("conversation/{idConversa:guid}")]
        [RequirePermission("WhatsApp", "visualizar")]
        public async Task<IActionResult> ObterConversa(Guid idConversa, [FromQuery] int ultimas = 20)
        {
            var estabelecimentoId = HttpContext.GetEstabelecimentoId();
            if (!estabelecimentoId.HasValue)
            {
                return BadRequest(new { error = "Selecione um estabelecimento para consultar conversas." });
            }

            var conversa = await _conversationRepository.ObterPorIdAsync(idConversa, estabelecimentoId.Value);
            if (conversa == null)
            {
                return NotFound();
            }

            if (ultimas <= 0)
            {
                ultimas = 20;
            }

            var resposta = await _conversationService.ObterConversaRespostaAsync(idConversa, estabelecimentoId.Value, ultimas);
            return resposta == null ? NotFound() : Ok(resposta);
        }

        [HttpGet("health")]
        [RequirePermission("WhatsApp", "visualizar")]
        public IActionResult Saude([FromServices] AutomationHealthService servicoSaude)
        {
            var saude = servicoSaude.ObterSaude();
            return Ok(saude);
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
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
