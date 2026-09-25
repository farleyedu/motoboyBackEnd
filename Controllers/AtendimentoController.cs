using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Atendimento;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    /// <summary>
    /// Atendimento ligado ao pedido (Fase 5): configuracao, respostas rapidas, vinculo conversa-pedido e
    /// mensagens internas atendente-motoboy. O estabelecimento vem sempre do token.
    /// </summary>
    [Route("api/v2/atendimento")]
    [ApiController]
    public sealed class AtendimentoController : ControllerBase
    {
        private readonly AtendimentoService _service;

        public AtendimentoController(AtendimentoService service)
        {
            _service = service;
        }

        // ---- configuracao ------------------------------------------------------

        [HttpGet("config")]
        [RequirePermission("WhatsApp", "visualizar")]
        public Task<IActionResult> GetConfig() => Run((est, _) => _service.GetConfigAsync(est));

        [HttpPut("config")]
        [RequirePermission("Delivery", "configurar")]
        public Task<IActionResult> UpdateConfig([FromBody] UpdateAtendimentoConfigRequest? request) =>
            Run((est, user) => _service.UpdateConfigAsync(est, user, request));

        // ---- respostas rapidas -------------------------------------------------

        /// <summary>Respostas ativas (padrao) ou todas (<c>todas=true</c>, para a tela de configuracao).</summary>
        [HttpGet("respostas-rapidas")]
        [RequirePermission("WhatsApp", "visualizar")]
        public Task<IActionResult> ListRespostas([FromQuery] bool todas = false) =>
            Run((est, _) => _service.ListRespostasAsync(est, onlyActive: !todas));

        [HttpPost("respostas-rapidas")]
        [RequirePermission("Delivery", "configurar")]
        public Task<IActionResult> CreateResposta([FromBody] SalvarRespostaRapidaRequest? request) =>
            Run((est, _) => _service.CreateRespostaAsync(est, request));

        [HttpPut("respostas-rapidas/{id:guid}")]
        [RequirePermission("Delivery", "configurar")]
        public Task<IActionResult> UpdateResposta(Guid id, [FromBody] SalvarRespostaRapidaRequest? request) =>
            Run((est, _) => _service.UpdateRespostaAsync(est, id, request));

        [HttpDelete("respostas-rapidas/{id:guid}")]
        [RequirePermission("Delivery", "configurar")]
        public Task<IActionResult> DeleteResposta(Guid id) =>
            Run(async (est, _) =>
            {
                await _service.DeleteRespostaAsync(est, id);
                return new { };
            });

        /// <summary>Troca as variaveis ({numero}, {cliente}, {motoboy}, {previsao}, {total}, {loja}) pelos dados do pedido.</summary>
        [HttpPost("respostas-rapidas/renderizar")]
        [RequirePermission("WhatsApp", "visualizar")]
        public Task<IActionResult> Render([FromBody] RenderRespostaRequest? request) =>
            Run((est, _) => _service.RenderAsync(est, request));

        // ---- conversa <-> pedido -----------------------------------------------

        [HttpGet("pedidos/{pedidoId:int}/conversa")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> GetConversaDoPedido(int pedidoId) =>
            Run((est, _) => _service.GetConversaDoPedidoAsync(est, pedidoId));

        /// <summary>Abre (ou cria) a conversa do cliente do pedido pelo telefone e liga o pedido a ela.</summary>
        [HttpPost("pedidos/{pedidoId:int}/conversa")]
        [RequirePermission("WhatsApp", "visualizar")]
        public Task<IActionResult> AbrirConversaDoPedido(int pedidoId) =>
            Run((est, _) => _service.AbrirConversaDoPedidoAsync(est, pedidoId));

        [HttpPost("conversas/{conversaId:guid}/pedidos/{pedidoId:int}/vincular")]
        [RequirePermission("Delivery", "editar_pedido")]
        public Task<IActionResult> Vincular(Guid conversaId, int pedidoId) =>
            Run((est, _) => _service.VincularAsync(est, conversaId, pedidoId));

        // ---- mensagens atendente <-> motoboy -----------------------------------

        [HttpGet("motoboy-atalhos")]
        [RequirePermission("Delivery", "visualizar")]
        public IActionResult OperatorShortcuts() => Ok(ApiResponse<object>.Ok(AtendimentoService.Shortcuts(operatorSide: true)));

        [HttpGet("motoboys/mensagens")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> ListMessages([FromQuery] int? motoboyId, [FromQuery] int? pedidoId, [FromQuery] int limit = 50) =>
            Run((est, _) => _service.ListMessagesAsync(est, motoboyId, pedidoId, limit));

        [HttpPost("motoboys/{motoboyId:int}/mensagens")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public Task<IActionResult> SendToMotoboy(int motoboyId, [FromBody] SendMotoboyMessageRequest? request) =>
            Run((est, user) => _service.SendToMotoboyAsync(est, user, motoboyId, request));

        [HttpPost("motoboys/{motoboyId:int}/mensagens/lidas")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> MarkRead(int motoboyId) =>
            Run(async (est, _) => new { marcadas = await _service.MarkReadAsync(est, motoboyId, readerIsMotoboy: false) });

        private async Task<IActionResult> Run<T>(Func<Guid, int, Task<T>> action)
        {
            var userId = HttpContext.GetUserId() ?? 0;
            var estabelecimentoId = HttpContext.GetEstabelecimentoId() ?? Guid.Empty;
            if (userId <= 0 || estabelecimentoId == Guid.Empty)
            {
                return Unauthorized(ApiResponse<object>.Fail("Contexto autenticado invalido.", "UNAUTHENTICATED"));
            }

            try
            {
                return Ok(ApiResponse<T>.Ok(await action(estabelecimentoId, userId)));
            }
            catch (DeliveryDomainException ex)
            {
                return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details));
            }
        }
    }
}
