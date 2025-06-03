using APIBack.DTOs;
using APIBack.Model;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class PedidoController : Controller
    {
        readonly IPedidoService _pedidoService;
        public PedidoController(IPedidoService pedidoService)
        {
            _pedidoService = pedidoService;
        }
        // GET: api/pedidos
        [HttpGet]
        public ActionResult<IEnumerable<Pedido>> GetPedidos()
        {
            var pedidos = _pedidoService.GetPedidos();
            return Ok(pedidos);
        }

        // GET: api/pedidos/1
        [HttpGet("{id}")]
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
        public ActionResult<IEnumerable<PedidoDTOs>> GetPedidosComMotoboy()
        {
            var pedidos = _pedidoService.GetPedidosMaps();
            return Ok(pedidos);
        }

        // POST: api/pedidos
        [HttpPost]
        public ActionResult<Pedido> PostPedido(Pedido pedido)
        {
            _pedidoService.CriarPedido();
            return CreatedAtAction(nameof(GetPedido), new { id = pedido.Id }, pedido);
        }
        // PUT: api/pedidos/1
        [HttpPut("{id}")]
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


    }
}
