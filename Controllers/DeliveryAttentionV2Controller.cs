using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Delivery;
using APIBack.Extensions;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    /// <summary>
    /// Atencao ao motoboy desconectado (turno encerrado com pedidos em aberto). Tudo e isolado pelo estabelecimento do token.
    /// </summary>
    [Route("api/v2/delivery/atencao-motoboy")]
    [ApiController]
    public sealed class DeliveryAttentionV2Controller : ControllerBase
    {
        private readonly DisconnectIncidentService _service;

        public DeliveryAttentionV2Controller(DisconnectIncidentService service) => _service = service;

        /// <summary>Incidentes abertos (padrao) ou todos (?status=todos), do mais recente ao mais antigo.</summary>
        [HttpGet]
        [RequireModule("Delivery")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> List([FromQuery] string? status, [FromQuery] DateTime? desde) =>
            ExecuteAsync((est, _) => _service.ListAsync(est,
                string.Equals(status, "todos", StringComparison.OrdinalIgnoreCase),
                desde.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(desde.Value, desde.Value.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : desde.Value.Kind)) : null,
                CanDecideExceptions()));

        [HttpGet("{incidenteId:guid}")]
        [RequireModule("Delivery")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> Get(Guid incidenteId) =>
            ExecuteAsync((est, _) => _service.GetAsync(est, incidenteId, CanDecideExceptions()));

        /// <summary>Aplica uma decisao da loja a um ou mais pedidos do incidente (lote parcial permitido).</summary>
        [HttpPost("{incidenteId:guid}/acoes")]
        [RequireModule("Delivery")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public Task<IActionResult> Act(Guid incidenteId, [FromBody] AcaoIncidenteRequest? request)
        {
            if (request != null && string.IsNullOrWhiteSpace(request.IdempotencyKey)
                && Request.Headers.TryGetValue("Idempotency-Key", out var header))
                request.IdempotencyKey = header.ToString();
            return ExecuteAsync((est, user) => _service.ActAsync(est, user, CanDecideExceptions(), incidenteId, request));
        }

        /// <summary>Decisoes excepcionais (cancelar/falha com a mercadoria ainda com o motoboy) exigem Delivery/configurar.</summary>
        private bool CanDecideExceptions() => HttpContext.IsSuperAdmin() || HttpContext.TemPermissao("Delivery", "configurar");

        private async Task<IActionResult> ExecuteAsync<T>(Func<Guid, int, Task<T>> action)
        {
            var userId = HttpContext.GetUserId() ?? 0;
            var estabelecimentoId = HttpContext.GetEstabelecimentoId() ?? Guid.Empty;
            if (userId <= 0 || estabelecimentoId == Guid.Empty)
                return Unauthorized(ApiResponse<object>.Fail("Contexto autenticado invalido.", "UNAUTHENTICATED"));
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
