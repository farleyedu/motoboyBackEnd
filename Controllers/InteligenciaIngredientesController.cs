using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Inteligencia;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    /// <summary>
    /// Gestão e Inteligência, ingredientes (docs/gestao/03-ingredientes.md). A loja vem sempre do token e o módulo
    /// Inteligencia precisa estar ativo. Ver = visualizar; cadastrar = criar; editar/inativar = editar; preço = informar_custo.
    /// </summary>
    [ApiController]
    [Route("api/v2/inteligencia/ingredientes")]
    public sealed class InteligenciaIngredientesController : EstabelecimentoScopedControllerBase
    {
        private readonly IIngredienteService _service;

        public InteligenciaIngredientesController(IIngredienteService service)
        {
            _service = service;
        }

        [HttpGet]
        [RequirePermission("Inteligencia", "visualizar")]
        public Task<IActionResult> List([FromQuery] IngredienteFiltro filtro) =>
            ExecuteAsync((est, _) => _service.ListAsync(est, filtro));

        [HttpGet("{id:guid}")]
        [RequirePermission("Inteligencia", "visualizar")]
        public Task<IActionResult> Get(Guid id) => ExecuteAsync((est, _) => _service.GetAsync(est, id));

        [HttpPost]
        [RequirePermission("Inteligencia", "criar")]
        public Task<IActionResult> Create([FromBody] IngredienteRequest request) =>
            ExecuteAsync((est, user) =>
            {
                // Quem só cadastra não informa preço: o custo exige a permissão própria.
                if (request?.Custo != null && !HasAnyPermission(("Inteligencia", "informar_custo")))
                    throw new DeliveryDomainException(403, "SEM_PERMISSAO_CUSTO", "Você não tem permissão para informar custos.");
                return _service.CreateAsync(est, user, request!);
            }, created: true);

        [HttpPut("{id:guid}")]
        [RequirePermission("Inteligencia", "editar")]
        public Task<IActionResult> Update(Guid id, [FromBody] IngredienteRequest request) =>
            ExecuteAsync((est, _) => _service.UpdateAsync(est, id, request));

        [HttpGet("{id:guid}/custos")]
        [RequirePermission("Inteligencia", "visualizar")]
        public Task<IActionResult> Custos(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 30) =>
            ExecuteAsync((est, _) => _service.ListCustosAsync(est, id, page, pageSize));

        [HttpPost("{id:guid}/custos")]
        [RequirePermission("Inteligencia", "informar_custo")]
        public Task<IActionResult> AddCusto(Guid id, [FromBody] CustoRequest request) =>
            ExecuteAsync((est, user) => _service.AddCustoAsync(est, user, id, request), created: true);

        [HttpGet("{id:guid}/usos")]
        [RequirePermission("Inteligencia", "visualizar")]
        public Task<IActionResult> Usos(Guid id) => ExecuteAsync((est, _) => _service.UsosAsync(est, id));

        [HttpPost("{id:guid}/inativar")]
        [RequirePermission("Inteligencia", "editar")]
        public Task<IActionResult> Inativar(Guid id) => ExecuteAsync((est, _) => _service.SetAtivoAsync(est, id, false));

        [HttpPost("{id:guid}/reativar")]
        [RequirePermission("Inteligencia", "editar")]
        public Task<IActionResult> Reativar(Guid id) => ExecuteAsync((est, _) => _service.SetAtivoAsync(est, id, true));

        private async Task<IActionResult> ExecuteAsync<T>(Func<Guid, int, Task<T>> action, bool created = false)
        {
            if (!TryResolveCurrentEstabelecimentoAndModule("Inteligencia", out var estabelecimentoId, out var error)) return error!;
            try
            {
                var result = await action(estabelecimentoId, CurrentUserId ?? 0);
                return created ? StatusCode(201, ApiResponse<T>.Ok(result)) : Ok(ApiResponse<T>.Ok(result));
            }
            catch (DeliveryDomainException ex)
            {
                return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details));
            }
        }
    }
}
