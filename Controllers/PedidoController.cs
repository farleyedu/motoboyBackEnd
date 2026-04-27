using APIBack.DTOs;
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
        private readonly IHubContext<DeliveryHub> _deliveryHub;

        // Simulação de repositório em memória (trocar pelo seu contexto do EF depois)
        private static readonly List<string> PedidosRegistrados = new();
        public PedidoController(IPedidoService pedidoService, IHubContext<DeliveryHub> deliveryHub)
        {
            _pedidoService = pedidoService;
            _deliveryHub = deliveryHub;
        }
        // GET: api/pedidos
        [HttpGet]
        [RequirePermission("Delivery", "visualizar")]
        public ActionResult<IEnumerable<Pedido>> GetPedidos()
        {
            var pedidos = _pedidoService.GetPedidos();
            return Ok(pedidos);
        }

        // GET: api/pedidos/1
        [HttpGet("{id}")]
        [RequirePermission("Delivery", "visualizar")]
        public ActionResult<Pedido> GetPedido(int id)
        {
            var pedido = _pedidoService.GetPedidosId(id);
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
            var pedidos = _pedidoService.GetPedidosMaps();
            return Ok(pedidos);
        }

        // POST: api/pedidos
        [HttpPost]
        [RequirePermission("Delivery", "criar_pedido")]
        public ActionResult<Pedido> PostPedido(Pedido pedido)
        {
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
                // Validação básica do ID
                if (id < 1)
                {
                    return BadRequest(new
                    {
                        success = false,
                        error = "ID do pedido deve ser maior que zero",
                        traceId = HttpContext.TraceIdentifier
                    });
                }

                // Busca o pedido completo
                var pedidoCompleto = await _pedidoService.GetPedidoCompleto(id);
                
                if (pedidoCompleto == null)
                {
                    return NotFound(new
                    {
                        success = false,
                        error = $"Pedido com ID {id} não encontrado",
                        traceId = HttpContext.TraceIdentifier
                    });
                }

                // Retorna o pedido completo
                return Ok(new
                {
                    success = true,
                    data = pedidoCompleto,
                    traceId = HttpContext.TraceIdentifier
                });
            }
            catch (Exception ex)
            {
                // Log estruturado do erro
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
        /// Obtém o pedido completo mais recente com todos os detalhes (endpoint para motoboy)
        /// </summary>
        /// <returns>Dados completos do pedido</returns>
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
                var pedidosCompletos = await _pedidoService.GetTodosPedidosCompletos();

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
            var pedidoExistente = _pedidoService.GetPedidosId(id);
            if (pedidoExistente == null)
            {
                return NotFound();
            }

            _pedidoService.AlteraPedido(id, pedido);
            return NoContent();
        }

        [HttpPut("AtribuirMotoboy")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public async Task<IActionResult> AtribuirMotoboy([FromBody] EnviarPedidosParaRotaDTO dto)
        {
            if (dto.PedidosIds == null || !dto.PedidosIds.Any())
                return BadRequest("Nenhum pedido informado.");

            await _pedidoService.AtribuirMotoboy(dto);

            var estabelecimentoId = HttpContext.GetEstabelecimentoId();
            if (estabelecimentoId.HasValue && estabelecimentoId.Value != Guid.Empty)
            {
                var evt = new DeliveryRouteAssignedRealtimeDto
                {
                    MotoboyId = dto.MotoboyResponsavel,
                    EstabelecimentoId = estabelecimentoId.Value,
                    PedidoIds = dto.PedidosIds.ToList(),
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };

                await _deliveryHub.Clients
                    .Group(DeliveryRealtimeEvents.EstablishmentGroup(estabelecimentoId.Value))
                    .SendAsync(DeliveryRealtimeEvents.DeliveryRouteAssigned, evt);

                await _deliveryHub.Clients
                    .Group(DeliveryRealtimeEvents.EstablishmentGroup(estabelecimentoId.Value))
                    .SendAsync(DeliveryRealtimeEvents.DeliveryOrderUpdated, evt);
            }

            return NoContent();
        }



        [HttpPost("PedidoIfood")]
        [RequirePermission("Delivery", "integracao_ifood")]
        public async Task<IActionResult> CriarPedidosIfood(PedidoCapturado pedidos)
        {
            if (pedidos == null)
                return BadRequest("Lista vazia.");

            await _pedidoService.CriarPedidosIfood(pedidos); // 👈 agora com await
            return Ok(new {});
        }

    }

}
