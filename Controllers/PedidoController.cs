using APIBack.DTOs;
using APIBack.DTOs.Common;
using APIBack.DTOs.Delivery;
using APIBack.Model;
using APIBack.Attributes;
using APIBack.DTOs.Tracking;
using APIBack.Extensions;
using APIBack.Hubs;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace APIBack.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class PedidoController : Controller
    {
        readonly IPedidoService _pedidoService;
        readonly IPedidoQueueService _pedidoQueueService;
        private readonly IHubContext<DeliveryHub> _deliveryHub;

        public PedidoController(IPedidoService pedidoService, IPedidoQueueService pedidoQueueService, IHubContext<DeliveryHub> deliveryHub)
        {
            _pedidoService = pedidoService;
            _pedidoQueueService = pedidoQueueService;
            _deliveryHub = deliveryHub;
        }

        private bool TryGetEstabelecimentoId(out Guid estabelecimentoId, out ActionResult? error)
        {
            estabelecimentoId = HttpContext.GetEstabelecimentoId() ?? Guid.Empty;
            if (estabelecimentoId == Guid.Empty)
            {
                error = Unauthorized(ApiResponse<object>.Fail("Contexto autenticado invalido.", "UNAUTHENTICATED"));
                return false;
            }
            error = null;
            return true;
        }

        // GET: api/pedidos
        [HttpGet]
        [RequirePermission("Delivery", "visualizar")]
        public ActionResult<IEnumerable<Pedido>> GetPedidos()
        {
            if (!TryGetEstabelecimentoId(out var estabelecimentoId, out var error)) return error!;
            var pedidos = _pedidoService.GetPedidos(estabelecimentoId);
            return Ok(pedidos);
        }

        // GET: api/pedidos/1
        [HttpGet("{id}")]
        [RequirePermission("Delivery", "visualizar")]
        public ActionResult<Pedido> GetPedido(int id)
        {
            if (!TryGetEstabelecimentoId(out var estabelecimentoId, out var error)) return error!;
            var pedido = _pedidoService.GetPedidosId(id, estabelecimentoId);
            if (pedido == null)
            {
                return NotFound();
            }
            return Ok(pedido);
        }

        [HttpGet("pedidosMaps")]
        [RequirePermission("Delivery", "visualizar")]
        public ActionResult<IEnumerable<PedidoDTOs>> GetPedidosComMotoboy()
        {
            if (!TryGetEstabelecimentoId(out var estabelecimentoId, out var error)) return error!;
            var pedidos = _pedidoService.GetPedidosMaps(estabelecimentoId);
            return Ok(pedidos);
        }

        // POST: api/pedidos
        [HttpPost]
        [RequirePermission("Delivery", "criar_pedido")]
        public ActionResult<Pedido> PostPedido(Pedido pedido)
        {
            // Criacao manual pelo atendente ainda nao foi implementada no backend
            // (fora do escopo de fila/rota/atribuicao da Parte 2). CriarPedido()
            // continua lancando NotImplementedException de proposito.
            _pedidoService.CriarPedido();
            return CreatedAtAction(nameof(GetPedido), new { id = pedido.Id }, pedido);
        }

        /// <summary>
        /// Obtém pedido completo com todos os detalhes (endpoint riderlink)
        /// </summary>
        /// <param name="id">ID do pedido</param>
        /// <returns>Dados completos do pedido</returns>
        [HttpGet("{id}/riderlink")]
        [RequirePermission("Delivery", "visualizar")]
        public async Task<IActionResult> GetPedidoCompleto(int id)
        {
            try
            {
                if (id < 1)
                {
                    return BadRequest(new
                    {
                        success = false,
                        error = "ID do pedido deve ser maior que zero",
                        traceId = HttpContext.TraceIdentifier
                    });
                }

                if (!TryGetEstabelecimentoId(out var estabelecimentoId, out var authError)) return authError!;

                var pedidoCompleto = await _pedidoService.GetPedidoCompleto(id, estabelecimentoId);

                if (pedidoCompleto == null)
                {
                    return NotFound(new
                    {
                        success = false,
                        error = $"Pedido com ID {id} não encontrado",
                        traceId = HttpContext.TraceIdentifier
                    });
                }

                return Ok(new
                {
                    success = true,
                    data = pedidoCompleto,
                    traceId = HttpContext.TraceIdentifier
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Erro ao buscar pedido completo {id}: {ex.Message}");

                return StatusCode(500, new
                {
                    success = false,
                    error = "Erro interno do servidor",
                    traceId = HttpContext.TraceIdentifier
                });
            }
        }

        /// <summary>
        /// Obtém a lista completa de pedidos com todos os detalhes (endpoint para motoboy)
        /// </summary>
        /// <returns>Lista de dados completos dos pedidos</returns>
        [HttpGet("motoboy")]
        [RequirePermission("Delivery", "visualizar")]
        public async Task<IActionResult> GetPedidosCompletos()
        {
            try
            {
                if (!TryGetEstabelecimentoId(out var estabelecimentoId, out var authError)) return authError!;

                var pedidosCompletos = await _pedidoService.GetTodosPedidosCompletos(estabelecimentoId);

                if (pedidosCompletos == null || !pedidosCompletos.Any())
                {
                    return NotFound(new
                    {
                        success = false,
                        error = "Nenhum pedido completo encontrado",
                        traceId = HttpContext.TraceIdentifier
                    });
                }

                return Ok(new
                {
                    success = true,
                    data = pedidosCompletos,
                    traceId = HttpContext.TraceIdentifier
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Erro ao buscar lista de pedidos completos: {ex.Message}");

                return StatusCode(500, new
                {
                    success = false,
                    error = "Erro interno do servidor",
                    traceId = HttpContext.TraceIdentifier
                });
            }
        }

        // PUT: api/pedidos/1
        [HttpPut("{id}")]
        [RequirePermission("Delivery", "editar_pedido")]
        public IActionResult PutPedido(int id, Pedido pedido)
        {
            if (!TryGetEstabelecimentoId(out var estabelecimentoId, out var error)) return error!;
            var pedidoExistente = _pedidoService.GetPedidosId(id, estabelecimentoId);
            if (pedidoExistente == null)
            {
                return NotFound();
            }

            _pedidoService.AlteraPedido(id, pedido);
            return NoContent();
        }

        /// <summary>
        /// Endpoint legado mantido por compatibilidade. Agora delega para o comando
        /// transacional de fila (IPedidoQueueService.AssignAsync), que valida tenant,
        /// vinculo e sessao online do motoboy e mantem fila/posicao consistentes -
        /// a UPDATE direta que existia aqui antes nao fazia nenhuma dessas checagens.
        /// Novas integracoes devem usar POST /api/v2/delivery/pedidos/{id}/atribuir.
        /// </summary>
        [HttpPut("AtribuirMotoboy")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public async Task<IActionResult> AtribuirMotoboy([FromBody] EnviarPedidosParaRotaDTO dto)
        {
            if (dto.PedidosIds == null || !dto.PedidosIds.Any())
                return BadRequest("Nenhum pedido informado.");

            if (!TryGetEstabelecimentoId(out var estabelecimentoId, out var error)) return error!;
            var actorUserId = HttpContext.GetUserId() ?? 0;

            foreach (var pedidoId in dto.PedidosIds)
            {
                try
                {
                    await _pedidoQueueService.AssignAsync(estabelecimentoId, actorUserId, dto.MotoboyResponsavel, pedidoId);
                }
                catch (DeliveryDomainException ex)
                {
                    return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details));
                }
            }

            var evt = new DeliveryRouteAssignedRealtimeDto
            {
                MotoboyId = dto.MotoboyResponsavel,
                EstabelecimentoId = estabelecimentoId,
                PedidoIds = dto.PedidosIds.ToList(),
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            await _deliveryHub.Clients
                .Group(DeliveryRealtimeEvents.EstablishmentGroup(estabelecimentoId))
                .SendAsync(DeliveryRealtimeEvents.DeliveryRouteAssigned, evt);

            await _deliveryHub.Clients
                .Group(DeliveryRealtimeEvents.EstablishmentGroup(estabelecimentoId))
                .SendAsync(DeliveryRealtimeEvents.DeliveryOrderUpdated, evt);

            return NoContent();
        }

        [HttpPost("PedidoIfood")]
        [RequirePermission("Delivery", "integracao_ifood")]
        public async Task<IActionResult> CriarPedidosIfood(PedidoCapturado pedidos)
        {
            if (pedidos == null)
                return BadRequest("Lista vazia.");

            if (!TryGetEstabelecimentoId(out var estabelecimentoId, out var error)) return error!;

            try
            {
                var created = await _pedidoService.CriarPedidosIfood(pedidos, estabelecimentoId);
                return Ok(new { success = true, created });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Erro ao criar pedido iFood: {ex.Message}");
                return StatusCode(500, new { success = false, error = "Erro interno do servidor" });
            }
        }

    }

}
