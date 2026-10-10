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
        private readonly IClienteEnderecoRepository _enderecos;

        public ClientesController(IClienteCadastroRepository clientes, IClienteEnderecoRepository enderecos)
        {
            _clientes = clientes;
            _enderecos = enderecos;
        }

        [HttpGet]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> List([FromQuery] ClienteFiltroRequest filtro) =>
            ExecuteAsync((est, _) => _clientes.ListAsync(est, filtro.Q, filtro.IncluirInativos, filtro.Page, filtro.PageSize));

        [HttpGet("{clienteId:guid}")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> Get(Guid clienteId) =>
            ExecuteAsync(async (est, _) => await _clientes.GetAsync(est, clienteId) ?? throw NotFound_());

        [HttpGet("{clienteId:guid}/enderecos")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> Enderecos(Guid clienteId) => ExecuteAsync((est, _) => _enderecos.ListAsync(est, clienteId));

        [HttpPost("{clienteId:guid}/enderecos")]
        [RequirePermission("Delivery", "criar_pedido")]
        public Task<IActionResult> AddEndereco(Guid clienteId, ClienteEnderecoRequest body) =>
            ExecuteAsync((est, _) => _enderecos.SaveAsync(est, clienteId, null, body), created: true);

        [HttpPut("{clienteId:guid}/enderecos/{id:guid}")]
        [RequirePermission("Delivery", "editar_pedido")]
        public Task<IActionResult> EditEndereco(Guid clienteId, Guid id, ClienteEnderecoRequest body) =>
            ExecuteAsync((est, _) => _enderecos.SaveAsync(est, clienteId, id, body));

        [HttpDelete("{clienteId:guid}/enderecos/{id:guid}")]
        [RequirePermission("Delivery", "editar_pedido")]
        public Task<IActionResult> DeleteEndereco(Guid clienteId, Guid id) => ExecuteAsync(async (est, _) =>
        { await _enderecos.DeleteAsync(est, clienteId, id); return new { id }; });

        [HttpPost]
        [RequirePermission("Delivery", "criar_pedido")]
        public Task<IActionResult> Create([FromBody] ClienteRequest request) =>
            ExecuteAsync((est, user) => _clientes.CreateAsync(est, user, ClienteRules.Validate(request)), created: true);

        [HttpPut("{clienteId:guid}")]
        [RequirePermission("Delivery", "editar_pedido")]
        public Task<IActionResult> Update(Guid clienteId, [FromBody] ClienteRequest request) =>
            ExecuteAsync(async (est, _) =>
                await _clientes.UpdateAsync(est, clienteId, ClienteRules.Validate(request)) ?? throw NotFound_());

        /// <summary>Edicao inline do nome, sem exigir o formulario completo (chat do modo comando, Fase 3c).</summary>
        [HttpPut("{clienteId:guid}/nome")]
        [RequirePermission("Delivery", "editar_pedido")]
        public Task<IActionResult> UpdateNome(Guid clienteId, [FromBody] ClienteNomeRequest request) =>
            ExecuteAsync(async (est, _) =>
                await _clientes.UpdateNomeAsync(est, clienteId, ClienteRules.ValidateNome(request.Nome)) ?? throw NotFound_());

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
