using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Orders;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

[ApiController, Route("api/v2/delivery/ifood"), RequirePermission("Delivery", "integracao_ifood")]
public sealed class IfoodOrderRefreshController(IfoodOrderRefreshService service) : ControllerBase
{
    [HttpPost("pedidos/{pedido:int}/refresh")]
    public async Task<IActionResult> Refresh(int pedido, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetEstabelecimentoId() is not Guid store || HttpContext.GetUserId() is not int user || user <= 0) return Unauthorized();
        try { await service.RefreshAsync(store, user, pedido, ct); return Ok(ApiResponse<object>.Ok(new { })); }
        catch (DeliveryDomainException ex) { return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code)); }
        catch (IfoodIntegrationException ex) { return StatusCode(503, ApiResponse<object>.Fail("Não foi possível atualizar os dados oficiais do pedido.", ex.Code)); }
    }
}
