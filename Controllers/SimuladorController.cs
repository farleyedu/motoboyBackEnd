using System;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Clientes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Simulador;
using APIBack.Extensions;
using APIBack.Repository;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    /// <summary>
    /// Simulador operacional (v2): hub, eventos, sessoes, cliente de teste e pedido de teste.
    /// Tudo usa os comandos reais do delivery; o simulador nao tem regra propria que o real nao tenha.
    /// </summary>
    [Route("api/v2/simulador")]
    [ApiController]
    [RequirePermission("Delivery", "gestao_motoboy")]
    public sealed class SimuladorController : ControllerBase
    {
        private readonly ISimuladorRepository _repo;
        private readonly IClienteSimulatorService _clientes;
        private readonly ISimuladorPedidoService _pedidos;
        private readonly ISimuladorIntegracoes _integracoes;

        public SimuladorController(
            ISimuladorRepository repo,
            IClienteSimulatorService clientes,
            ISimuladorPedidoService pedidos,
            ISimuladorIntegracoes integracoes)
        {
            _repo = repo;
            _clientes = clientes;
            _pedidos = pedidos;
            _integracoes = integracoes;
        }

        // ---------------------------------------------------------------- hub, eventos, sessoes

        [HttpGet("resumo")]
        public Task<IActionResult> Resumo() => ExecuteAsync((est, _) => _repo.GetResumoAsync(est));

        [HttpGet("integracoes")]
        public Task<IActionResult> Integracoes() => ExecuteAsync((est, _) => _integracoes.ListAsync(est));

        [HttpGet("eventos")]
        public Task<IActionResult> ListEventos(
            [FromQuery] string? entidade, [FromQuery(Name = "ref")] string? entidadeRef, [FromQuery] string? cenario,
            [FromQuery] string? status, [FromQuery] int limit = 50, [FromQuery] long? antesDe = null) =>
            ExecuteAsync((est, _) => _repo.ListEventosAsync(est, Clean(entidade), Clean(entidadeRef), Clean(cenario), Clean(status), limit, antesDe));

        [HttpPost("eventos")]
        public Task<IActionResult> AddEvento([FromBody] SimEventoRequest request) =>
            ExecuteAsync((est, user) =>
                _repo.AddEventoAsync(est, user, HttpContext.GetUserNome(), SimuladorEventoRules.Validate(request)), created: true);

        [HttpDelete("eventos")]
        public Task<IActionResult> ClearEventos([FromQuery] string? entidade, [FromQuery] string? cenario) =>
            ExecuteAsync(async (est, _) => new { removidos = await _repo.ClearEventosAsync(est, Clean(entidade), Clean(cenario)) });

        [HttpGet("sessoes")]
        public Task<IActionResult> ListSessoes([FromQuery] string? tipo, [FromQuery] bool apenasAtivas = true) =>
            ExecuteAsync((est, _) => _repo.ListSessoesAsync(est, Clean(tipo), apenasAtivas));

        [HttpPut("sessoes")]
        public Task<IActionResult> UpsertSessao([FromBody] SimSessaoRequest request) =>
            ExecuteAsync((est, user) => _repo.UpsertSessaoAsync(
                est, user, SimuladorEventoRules.ValidateSessaoTipo(request?.Tipo), Clean(request?.Ref), Clean(request?.Titulo),
                SimuladorEventoRules.SessaoEstado(request?.Estado)));

        [HttpPost("sessoes/{sessaoId:guid}/encerrar")]
        public Task<IActionResult> EncerrarSessao(Guid sessaoId) =>
            ExecuteAsync(async (est, _) =>
            {
                if (!await _repo.EncerrarSessaoAsync(est, sessaoId)) throw new DeliveryDomainException(404, "SESSION_NOT_FOUND", "Sessao nao encontrada.");
                return new { id = sessaoId };
            });

        // ---------------------------------------------------------------- cliente de teste

        [HttpGet("clientes")]
        public Task<IActionResult> ListClientes([FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int pageSize = 12) =>
            ExecuteAsync((est, _) => _clientes.ListWithStatsAsync(est, q, page, pageSize));

        [HttpGet("clientes/{clienteId:guid}")]
        public Task<IActionResult> GetCliente(Guid clienteId) => ExecuteAsync((est, _) => _clientes.GetAsync(est, clienteId));

        [HttpGet("clientes/{clienteId:guid}/conversa")]
        public Task<IActionResult> GetConversa(Guid clienteId) => ExecuteAsync((est, _) => _clientes.GetConversaAsync(est, clienteId));

        [HttpGet("clientes/{clienteId:guid}/pedidos")]
        public Task<IActionResult> GetClientePedidos(Guid clienteId, [FromQuery] int limit = 10) =>
            ExecuteAsync((est, _) => _clientes.GetPedidosAsync(est, clienteId, limit));

        [HttpGet("clientes/{clienteId:guid}/mensagens")]
        public Task<IActionResult> GetMensagens(Guid clienteId, [FromQuery] int limit = 100) =>
            ExecuteAsync((est, _) => _clientes.GetMessagesAsync(est, clienteId, limit));

        [HttpPost("clientes/{clienteId:guid}/mensagens")]
        public Task<IActionResult> SendMensagem(Guid clienteId, [FromBody] SimulatorClienteMessageRequest request) =>
            ExecuteAsync((est, user) => _clientes.SendMessageAsync(est, user, clienteId, request?.Texto), accepted: true);

        // ---------------------------------------------------------------- pedido de teste

        [HttpGet("pedidos")]
        public Task<IActionResult> ListPedidos([FromQuery] string? q, [FromQuery] string? status, [FromQuery] int limit = 30) =>
            ExecuteAsync((est, _) => _pedidos.ListAsync(est, q, status, limit));

        [HttpGet("pedidos/{pedidoId:int}")]
        public Task<IActionResult> GetPedido(int pedidoId) => ExecuteAsync((est, _) => _pedidos.GetAsync(est, pedidoId));

        [HttpPost("pedidos")]
        public Task<IActionResult> CreatePedido([FromBody] SimPedidoCriarRequest request) =>
            ExecuteAsync((est, user) => _pedidos.CreateAsync(est, user, request), created: true);

        [HttpPut("pedidos/{pedidoId:int}")]
        public Task<IActionResult> AlterarPedido(int pedidoId, [FromBody] SimPedidoCriarRequest request) =>
            ExecuteAsync((est, user) => _pedidos.AlterarAsync(est, user, pedidoId, request));

        [HttpPost("pedidos/{pedidoId:int}/etapa")]
        public Task<IActionResult> Etapa(int pedidoId, [FromBody] SimPedidoEtapaRequest request) =>
            ExecuteAsync((est, user) => _pedidos.EtapaAsync(est, user, pedidoId, request));

        [HttpPost("pedidos/{pedidoId:int}/status")]
        public Task<IActionResult> Status(int pedidoId, [FromBody] SimPedidoStatusRequest request) =>
            ExecuteAsync((est, user) => _pedidos.StatusAsync(est, user, pedidoId, request));

        [HttpPost("pedidos/{pedidoId:int}/clonar")]
        public Task<IActionResult> Clonar(int pedidoId) => ExecuteAsync((est, user) => _pedidos.CloneAsync(est, user, pedidoId), created: true);

        [HttpPost("pedidos/{pedidoId:int}/injetar")]
        public Task<IActionResult> Injetar(int pedidoId) => ExecuteAsync((est, user) => _pedidos.InjectAsync(est, user, pedidoId));

        [HttpPost("pedidos/{pedidoId:int}/evento")]
        public Task<IActionResult> EventoPedido(int pedidoId, [FromBody] SimPedidoEventoRequest request) =>
            ExecuteAsync((est, user) => _pedidos.EventoAsync(est, user, pedidoId, request));

        [HttpPost("pedidos/{pedidoId:int}/motoboy")]
        public Task<IActionResult> MotoboyPedido(int pedidoId, [FromBody] SimPedidoMotoboyRequest request) =>
            ExecuteAsync((est, user) => _pedidos.MotoboyAsync(est, user, pedidoId, request?.MotoboyId ?? 0));

        [HttpPost("pedidos/{pedidoId:int}/anexar-cliente")]
        public Task<IActionResult> AnexarCliente(int pedidoId, [FromBody] SimPedidoAnexarRequest request) =>
            ExecuteAsync((est, user) => _pedidos.AnexarClienteAsync(est, user, pedidoId, request?.ClienteId ?? Guid.Empty));

        [HttpDelete("pedidos/{pedidoId:int}")]
        public Task<IActionResult> RemoverPedido(int pedidoId) =>
            ExecuteAsync(async (est, user) =>
            {
                await _pedidos.RemoverAsync(est, user, pedidoId);
                return new { id = pedidoId };
            });

        // ---------------------------------------------------------------- apoio

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private async Task<IActionResult> ExecuteAsync<T>(Func<Guid, int, Task<T>> action, bool created = false, bool accepted = false)
        {
            var userId = HttpContext.GetUserId() ?? 0;
            var estabelecimentoId = HttpContext.GetEstabelecimentoId() ?? Guid.Empty;
            if (userId <= 0 || estabelecimentoId == Guid.Empty)
            {
                return Unauthorized(ApiResponse<object>.Fail("Contexto autenticado invalido.", "UNAUTHENTICATED"));
            }

            try
            {
                var result = await action(estabelecimentoId, userId);
                return accepted ? StatusCode(202, ApiResponse<T>.Ok(result))
                    : created ? StatusCode(201, ApiResponse<T>.Ok(result))
                    : Ok(ApiResponse<T>.Ok(result));
            }
            catch (DeliveryDomainException ex)
            {
                return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details));
            }
        }
    }
}
