using System.ComponentModel.DataAnnotations;
using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Integrations.Ifood.Orders;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

[ApiController, Route("api/v2/delivery/ifood/pedidos/{pedido:int}/location"), RequirePermission("Delivery", "integracao_ifood")]
public sealed class IfoodLocationController(IfoodLocationService service, ILocalizacaoService location) : ControllerBase
{
    [HttpGet("search")]
    public async Task<IActionResult> Search(int pedido, [FromQuery, Required, StringLength(600)] string address, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetEstabelecimentoId() is not Guid store || HttpContext.GetUserId() is not int user || user <= 0) return Unauthorized();
        try
        {
            await service.GetAsync(store, pedido, ct); // valida loja e autorização antes da busca
            var result = await location.ConsultarEnderecoAsync(address);
            return Ok(ApiResponse<object>.Ok(result));
        }
        catch (DeliveryDomainException ex) { return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code)); }
    }
    [HttpGet]
    public async Task<IActionResult> Get(int pedido, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetEstabelecimentoId() is not Guid store || HttpContext.GetUserId() is not int user || user <= 0) return Unauthorized();
        try { return Ok(ApiResponse<object>.Ok(await service.GetAsync(store, pedido, ct))); }
        catch (DeliveryDomainException ex) { return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code)); }
    }

    [HttpPost]
    public async Task<IActionResult> Save(int pedido, IfoodLocationRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetEstabelecimentoId() is not Guid store || HttpContext.GetUserId() is not int user || user <= 0) return Unauthorized();
        try { await service.SaveAsync(store, user, pedido, request, ct); return Ok(ApiResponse<object>.Ok(new { })); }
        catch (ValidationException) { return BadRequest(ApiResponse<object>.Fail("Confira os campos do endereço e o motivo do ajuste.", "IFOOD_LOCATION_INVALID")); }
        catch (DeliveryDomainException ex) { return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code)); }
    }
}
