// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using APIBack.Automation.Services;
using APIBack.Automation.Validators;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Controllers
{
    [ApiController]
    [Route("automation")]
    public class AutomationController : ControllerBase
    {
        private readonly ILogger<AutomationController> _logger;
        private readonly ConversationService _servicoConversa;
        private readonly HandoverService _servicoHandover;
        private readonly IQueueBus _fila;
        private readonly IWhatsappSender _enviadorWhatsapp;
        private readonly IConversationRepository _repositorio;
        private readonly AgentReplyRequestValidator _validador = new();

        public AutomationController(
            ILogger<AutomationController> logger,
            ConversationService servicoConversa,
            HandoverService servicoHandover,
            IQueueBus fila,
            IWhatsappSender enviadorWhatsapp,
            IConversationRepository repositorio)
        {
            _logger = logger;
            _servicoConversa = servicoConversa;
            _servicoHandover = servicoHandover;
            _fila = fila;
            _enviadorWhatsapp = enviadorWhatsapp;
            _repositorio = repositorio;
        }

        [HttpPost("conversation/{idConversa:guid}/handover")]
        public async Task<IActionResult> EncaminharParaHumano(Guid idConversa, [FromBody] HandoverRequest req)
        {
            await _servicoHandover.DefinirHumanoAsync(idConversa, req.Agente, req.ReservaConfirmada, req.Detalhes);
            return Ok();
        }

        [HttpPost("conversation/{idConversa:guid}/back-to-bot")]
        public async Task<IActionResult> VoltarParaBot(Guid id)
        {
            await _servicoConversa.DefinirModoBotAsync(id, "Transição para bot");
            return Ok();
        }

        // [Authorize(Roles="Atendente")] // TODO: enable when security is configured
        [HttpPost("agent/reply")]
        public async Task<IActionResult> RespostaAgente([FromBody] AgentReplyRequest req)
        {
            var (valido, erro) = _validador.Validar(req);
            if (!valido) return BadRequest(new { erro });

            var conversa = await _repositorio.ObterPorIdAsync(req.IdConversa);
            if (conversa == null) return NotFound();

            if (conversa.Modo != ModoConversa.Humano)
            {
                return Conflict(new { erro = "Conversa não está em modo humano" });
            }

            var mensagem = await _servicoConversa.AcrescentarSaidaAsync(conversa.IdConversa, conversa.IdWa, req.Mensagem);
            await _fila.PublicarSaidaAsync(mensagem);

            // stub send to WA
            _ = await _enviadorWhatsapp.EnviarTextoAsync(conversa.IdWa, req.Mensagem);

            return Ok();
        }

        [HttpGet("conversation/{idConversa:guid}")]
        public async Task<IActionResult> ObterConversa(Guid id, [FromQuery] int ultimas = 20)
        {
            if (ultimas <= 0) ultimas = 20;
            var resposta = await _servicoConversa.ObterConversaRespostaAsync(id, ultimas);
            if (resposta == null) return NotFound();
            return Ok(resposta);
        }

        [HttpGet("health")]
        public IActionResult Saude([FromServices] AutomationHealthService servicoSaude)
        {
            var saude = servicoSaude.ObterSaude();
            return Ok(saude);
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
