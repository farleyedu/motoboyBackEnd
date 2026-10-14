using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Delivery;
using APIBack.Extensions;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Commands;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

[ApiController,Route("api/v2/delivery/ifood")]
public sealed class IfoodCommandsController(IfoodCommandService service) : ControllerBase
{
    [HttpPost("pedidos/{pedidoId:int}/commands"),RequirePermission("Delivery","integracao_ifood")]
    public Task<IActionResult> Submit(int pedidoId,IfoodCommandRequest request,CancellationToken ct)
        => Run(async(store,user)=>await service.SubmitAsync(store,user,pedidoId,request,ct),true);
    [HttpGet("operations/{operationId:guid}"),RequirePermission("Delivery","integracao_ifood")]
    public Task<IActionResult> Read(Guid operationId,CancellationToken ct)
        => Run(async(store,_)=>await service.ReadAsync(store,operationId,ct) ?? throw new DeliveryDomainException(404,"IFOOD_COMMAND_NOT_FOUND","Operação não encontrada."),false);
    private async Task<IActionResult> Run(Func<Guid,int,Task<IfoodCommandDto>> action,bool accepted)
    {
        Response.Headers.CacheControl="no-store";
        if(HttpContext.GetEstabelecimentoId() is not Guid store || HttpContext.GetUserId() is not int user || user<=0) return Unauthorized();
        try { var result=ApiResponse<IfoodCommandDto>.Ok(await action(store,user));return accepted?StatusCode(202,result):Ok(result); }
        catch(DeliveryDomainException ex) { return StatusCode(ex.StatusCode,ApiResponse<object>.Fail(ex.Message,ex.Code)); }
        catch(IfoodIntegrationException ex) { return StatusCode(503,ApiResponse<object>.Fail("Não foi possível consultar a operação iFood.",ex.Code)); }
    }
}
