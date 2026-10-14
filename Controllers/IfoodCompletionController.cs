using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Delivery;
using APIBack.Extensions;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Completion;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

[ApiController, Route("api/v2/motoboys/me/session/completion/ifood"), RequireOperationalSession]
public sealed class IfoodCompletionController(IfoodCompletionService service) : ControllerBase
{
    [HttpPost, RequestSizeLimit(16_384)]
    public async Task<IActionResult> Complete(DeliveryCompletionRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var payload = HttpContext.GetJwtPayload();
        if (payload.MotoboyId is not int rider || payload.UserId is not > 0 || payload.EstabelecimentoId is not Guid store ||
            !payload.MotoboySessionId.HasValue || !payload.SessionEpoch.HasValue) return Unauthorized();
        try
        {
            var operation = await service.EnqueueAsync(store, rider, payload.UserId.Value, payload.MotoboySessionId.Value,
                payload.SessionEpoch.Value, request, ct);
            return StatusCode(operation.State == "succeeded" ? 200 : 202, ApiResponse<IfoodCompletionOperationDto>.Ok(operation));
        }
        catch (DeliveryDomainException ex) { return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code)); }
        catch (IfoodIntegrationException ex) { return Conflict(ApiResponse<object>.Fail("Confirmação oficial indisponível. Atualize a entrega.", ex.Code)); }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
        { return Conflict(ApiResponse<object>.Fail("Consulte a operação existente antes de reenviar.", "IFOOD_COMPLETION_CONFLICT")); }
    }
}

// Recuperação por conta: continua disponível após expiração/encerramento do turno.
[ApiController, Route("api/motoboys/me/ifood-completions"), APIBack.Attributes.Authorize]
public sealed class IfoodCompletionRecoveryController(IfoodCompletionService service) : ControllerBase
{
    [HttpGet("{operation:guid}")]
    public async Task<IActionResult> Get(Guid operation, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var user = HttpContext.GetUserId();
        if (user is not > 0) return Unauthorized();
        var result = await service.GetAsync(user.Value, operation, ct);
        return result == null ? NotFound(ApiResponse<object>.Fail("Operação não localizada.", "IFOOD_COMPLETION_NOT_FOUND")) :
            Ok(ApiResponse<IfoodCompletionOperationDto>.Ok(result));
    }
}

[ApiController, Route("api/v2/delivery/ifood"), RequirePermission("Delivery", "integracao_ifood")]
public sealed class IfoodPickupCompletionController(IfoodCompletionService service) : ControllerBase
{
    [HttpGet("pedidos/{pedido:int}/pickup-completion")]
    public Task<IActionResult> Context(int pedido, CancellationToken ct) => Run(async (store, _) =>
        await service.PickupContextAsync(store, pedido, ct), false);
    [HttpPost("pedidos/{pedido:int}/pickup-completion"), RequestSizeLimit(16_384)]
    public Task<IActionResult> Complete(int pedido, IfoodPickupCompletionRequest request, CancellationToken ct) => Run(async (store, user) =>
        await service.EnqueuePickupAsync(store, user, pedido, request, ct), true);
    [HttpGet("completion-operations/{operation:guid}")]
    public Task<IActionResult> Operation(Guid operation, CancellationToken ct) => Run(async (store, _) =>
        await service.GetForStoreAsync(store, operation, ct) ?? throw new DeliveryDomainException(404, "IFOOD_COMPLETION_NOT_FOUND", "Operação não localizada nesta loja."), false);
    private async Task<IActionResult> Run<T>(Func<Guid, int, Task<T>> action, bool accepted)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetEstabelecimentoId() is not Guid store || HttpContext.GetUserId() is not int user || user <= 0) return Unauthorized();
        try { var result = ApiResponse<T>.Ok(await action(store, user)); return accepted ? StatusCode(202, result) : Ok(result); }
        catch (DeliveryDomainException ex) { return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code)); }
        catch (IfoodIntegrationException ex) { return Conflict(ApiResponse<object>.Fail("Confirmação oficial indisponível.", ex.Code)); }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
        { return Conflict(ApiResponse<object>.Fail("Consulte a confirmação existente antes de reenviar.", "IFOOD_COMPLETION_CONFLICT")); }
    }
}
