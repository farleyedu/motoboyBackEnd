using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Clientes;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Repository.Interface;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    /// <summary>
    /// Cadastro de clientes do estabelecimento ativo. As permissoes reaproveitam as do Delivery:
    /// ver = visualizar; criar = criar_pedido; editar/inativar = editar_pedido.
    /// </summary>
    [Route("api/v2/clientes")]
    [ApiController]
    public sealed class ClientesController : ControllerBase
    {
        private readonly IClienteCadastroRepository _clientes;

        public ClientesController(IClienteCadastroRepository clientes)
        {
            _clientes = clientes;
        }

        [HttpGet]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> List([FromQuery] ClienteFiltroRequest filtro) =>
            ExecuteAsync((est, _) => _clientes.ListAsync(est, filtro.Q, filtro.IncluirInativos, filtro.Page, filtro.PageSize));

        [HttpGet("{clienteId:guid}")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> Get(Guid clienteId) =>
            ExecuteAsync(async (est, _) => await _clientes.GetAsync(est, clienteId) ?? throw NotFound_());

        [HttpPost]
        [RequirePermission("Delivery", "criar_pedido")]
        public Task<IActionResult> Create([FromBody] ClienteRequest request) =>
            ExecuteAsync((est, user) => _clientes.CreateAsync(est, user, ClienteRules.Validate(request)), created: true);

        [HttpPut("{clienteId:guid}")]
        [RequirePermission("Delivery", "editar_pedido")]
        public Task<IActionResult> Update(Guid clienteId, [FromBody] ClienteRequest request) =>
            ExecuteAsync(async (est, _) =>
                await _clientes.UpdateAsync(est, clienteId, ClienteRules.Validate(request)) ?? throw NotFound_());

        /// <summary>Exclusao logica: o cliente some das listas, o historico de pedidos continua.</summary>
        [HttpDelete("{clienteId:guid}")]
        [RequirePermission("Delivery", "editar_pedido")]
        public Task<IActionResult> Deactivate(Guid clienteId) =>
            ExecuteAsync(async (est, _) =>
            {
                if (!await _clientes.DeactivateAsync(est, clienteId)) throw NotFound_();
                return new { id = clienteId };
            });

        private static DeliveryDomainException NotFound_() =>
            new(404, "CLIENT_NOT_FOUND", "Cliente nao encontrado.");

        private async Task<IActionResult> ExecuteAsync<T>(Func<Guid, int, Task<T>> action, bool created = false)
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
                return created
                    ? StatusCode(201, ApiResponse<T>.Ok(result))
                    : Ok(ApiResponse<T>.Ok(result));
            }
            catch (DeliveryDomainException ex)
            {
                return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details));
            }
        }
    }
}
