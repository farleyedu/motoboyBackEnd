using System;
using System.Threading.Tasks;
using APIBack.DTOs.Common;
using APIBack.Hubs;
using APIBack.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace APIBack.Controllers
{
    /// <summary>Situacao comercial: a loja consulta a propria; so o super admin (Gestao) muda.</summary>
    [ApiController]
    public sealed class SituacaoComercialController : GestaoControllerBase
    {
        private readonly ISituacaoComercialService _service;
        private readonly IHubContext<DeliveryHub> _hub;
        private readonly DeliveryHubConnections _connections;

        public SituacaoComercialController(ISituacaoComercialService service, IHubContext<DeliveryHub> hub, DeliveryHubConnections connections)
        {
            _service = service;
            _hub = hub;
            _connections = connections;
        }

        /// <summary>Situacao da loja do token. Liberada mesmo com a loja bloqueada (o front mostra "conta suspensa").</summary>
        [HttpGet("api/v2/comercial/situacao")]
        public async Task<IActionResult> Minha()
        {
            if (!CurrentUserId.HasValue) return UnauthorizedResponse();
            if (!CurrentEstabelecimentoId.HasValue)
                return BadRequest(ApiResponse<object>.Fail("Selecione um estabelecimento.", "ESTABELECIMENTO_NAO_SELECIONADO"));
            var dto = await _service.ObterAsync(CurrentEstabelecimentoId.Value, HttpContext.RequestAborted);
            return dto == null ? NotFound(ApiResponse<object>.Fail("Estabelecimento nao encontrado.")) : Ok(ApiResponse<SituacaoComercialDto>.Ok(dto));
        }

        [HttpGet("api/gestao/comercial/testes-vencidos")]
        public async Task<IActionResult> TestesVencidos()
        {
            if (!CurrentUserId.HasValue) return UnauthorizedResponse();
            if (!CurrentIsSuperAdmin) return ForbiddenResponse();
            var lista = await _service.ListarTestesVencidosAsync(HttpContext.RequestAborted);
            return Ok(ApiResponse<System.Collections.Generic.IReadOnlyList<TesteVencidoDto>>.Ok(lista));
        }

        [HttpGet("api/gestao/estabelecimentos/{id:guid}/situacao")]
        public async Task<IActionResult> Obter(Guid id)
        {
            if (!CurrentUserId.HasValue) return UnauthorizedResponse();
            if (!CurrentIsSuperAdmin) return ForbiddenResponse();
            var dto = await _service.ObterAsync(id, HttpContext.RequestAborted);
            return dto == null ? NotFound(ApiResponse<object>.Fail("Estabelecimento nao encontrado.")) : Ok(ApiResponse<SituacaoComercialDto>.Ok(dto));
        }

        [HttpGet("api/gestao/estabelecimentos/{id:guid}/situacao/historico")]
        public async Task<IActionResult> Historico(Guid id)
        {
            if (!CurrentUserId.HasValue) return UnauthorizedResponse();
            if (!CurrentIsSuperAdmin) return ForbiddenResponse();
            var lista = await _service.ListarHistoricoAsync(id, HttpContext.RequestAborted);
            return Ok(ApiResponse<System.Collections.Generic.IReadOnlyList<SituacaoComercialHistoricoDto>>.Ok(lista));
        }

        /// <summary>acao: iniciar_operacao | ativar (plano, pacotesMotoboy) | bloquear (motivo) | desbloquear. Tudo manual.</summary>
        [HttpPost("api/gestao/estabelecimentos/{id:guid}/situacao")]
        public async Task<IActionResult> Aplicar(Guid id, [FromBody] AcaoSituacaoRequest request)
        {
            if (!CurrentUserId.HasValue) return UnauthorizedResponse();
            if (!CurrentIsSuperAdmin) return ForbiddenResponse();
            try
            {
                var dto = await _service.AplicarAsync(id, request, CurrentUserId, HttpContext.RequestAborted);
                // Conexoes em tempo real ja abertas: o painel da loja recebe o aviso e para (a reconexao ja e barrada
                // pelo SituacaoComercialMiddleware). O app do motoboy fica de fora para terminar as entregas em rota.
                await _hub.Clients.Group(DeliveryRealtimeEvents.EstablishmentGroup(id))
                    .SendAsync(DeliveryRealtimeEvents.EstabelecimentoSituacaoAlterada, dto, HttpContext.RequestAborted);
                if (dto.Situacao == SituacoesComerciais.Bloqueado) _connections.Abort(id);
                return Ok(ApiResponse<SituacaoComercialDto>.Ok(dto));
            }
            catch (SituacaoComercialException ex)
            {
                return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Code));
            }
        }
    }
}
