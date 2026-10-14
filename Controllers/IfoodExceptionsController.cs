using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Delivery;
using APIBack.Extensions;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Exceptions;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

[ApiController,Route("api/v2/delivery/ifood/pedidos/{pedidoId:int}")]
public sealed class IfoodExceptionsController(IfoodExceptionService service) : ControllerBase
{
    [HttpGet("cancellation-reasons"),RequirePermission("Delivery","integracao_ifood")]
    public Task<IActionResult> Reasons(int pedidoId,CancellationToken ct)=>Run(async(s,_)=>await service.ReasonsAsync(s,pedidoId,ct),false);
    [HttpPost("cancellation"),RequirePermission("Delivery","integracao_ifood")]
    public Task<IActionResult> Cancel(int pedidoId,IfoodCancellationRequest request,CancellationToken ct)=>Run(async(s,u)=>await service.CancelAsync(s,u,pedidoId,request,ct),true);
    [HttpGet("disputes/{disputeId:guid}"),RequirePermission("Delivery","integracao_ifood")]
    public Task<IActionResult> Options(int pedidoId,Guid disputeId,CancellationToken ct)=>Run(async(s,_)=>await service.OptionsAsync(s,pedidoId,disputeId,ct),false);
    [HttpPost("disputes/{disputeId:guid}/response"),RequirePermission("Delivery","integracao_ifood")]
    public Task<IActionResult> Respond(int pedidoId,Guid disputeId,IfoodDisputeResponseRequest request,CancellationToken ct)=>Run(async(s,u)=>await service.RespondAsync(s,u,pedidoId,disputeId,request,ct),true);
    private async Task<IActionResult> Run(Func<Guid,int,Task<object>> action,bool accepted)
    {
        Response.Headers.CacheControl="no-store";
        if(HttpContext.GetEstabelecimentoId() is not Guid store || HttpContext.GetUserId() is not int user || user<=0) return Unauthorized();
        try { var result=ApiResponse<object>.Ok(await action(store,user));return accepted?StatusCode(202,result):Ok(result); }
        catch(DeliveryDomainException ex) { return StatusCode(ex.StatusCode,ApiResponse<object>.Fail(ex.Message,ex.Code)); }
        catch(IfoodIntegrationException ex) { return StatusCode(503,ApiResponse<object>.Fail("Não foi possível consultar a solicitação iFood.",ex.Code)); }
    }
}
