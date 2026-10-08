using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Delivery;
using APIBack.Extensions;
using APIBack.Repository.Interface;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

[ApiController,Route("api/v2/motoboys/me/session/completion"),RequireOperationalSession]
public sealed class DeliveryCompletionController(IPedidoQueueRepository repository) : ControllerBase
{
    [HttpGet("{pedido:int}")]
    public Task<IActionResult> Context(int pedido,CancellationToken ct) => Run((s,r,u) => repository.GetCompletionContextAsync(s,r,pedido,ct));
    [HttpPost("{pedido:int}/code"),RequestSizeLimit(2048)]
    public Task<IActionResult> Code(int pedido,DeliveryCodeRequest request,CancellationToken ct) => Run((s,r,u) => repository.ValidateCompletionCodeAsync(s,r,pedido,request.Codigo,ct));
    [HttpPost("{pedido:int}/proof"),RequestSizeLimit(5_700_000)]
    public Task<IActionResult> Proof(int pedido,DeliveryProofRequest request,CancellationToken ct) => Run((s,r,u) => repository.SaveCompletionProofAsync(s,r,pedido,request.Base64,ct));
    [HttpGet("{pedido:int}/proof")]
    public Task<IActionResult> PendingProof(int pedido,CancellationToken ct) => Run((s,r,u) => repository.ReadPendingCompletionProofAsync(s,r,pedido,ct));
    [HttpPost,RequestSizeLimit(16_384)]
    public Task<IActionResult> Complete(DeliveryCompletionRequest request,CancellationToken ct) => Run((s,r,u) => repository.CompleteDeliveryAsync(s,r,u,HttpContext.GetJwtPayload().MotoboySessionId!.Value,HttpContext.GetJwtPayload().SessionEpoch!.Value,request,ct));
    private async Task<IActionResult> Run<T>(Func<Guid,int,int,Task<T>> action)
    {
        Response.Headers.CacheControl="no-store";
        var payload=HttpContext.GetJwtPayload();
        if(payload.MotoboyId is not int rider || payload.UserId is not >0 || payload.EstabelecimentoId is not Guid store) return Unauthorized(ApiResponse<object>.Fail("Contexto operacional inválido."));
        try { return Ok(ApiResponse<T>.Ok(await action(store,rider,payload.UserId.Value))); }
        catch(DeliveryDomainException ex) { return StatusCode(ex.StatusCode,ApiResponse<object>.Fail(ex.Message,ex.Code)); }
        catch(ArgumentException ex) { return BadRequest(ApiResponse<object>.Fail(ex.Message,"VALIDATION_ERROR")); }
        catch(Npgsql.PostgresException ex) when(ex.SqlState==Npgsql.PostgresErrorCodes.UniqueViolation) { return Conflict(ApiResponse<object>.Fail("Conclusão já registrada ou identificação reutilizada. Consulte o recibo.","COMPLETION_CONFLICT")); }
    }
}

[ApiController,Route("api/motoboys/me/receipts"),APIBack.Attributes.Authorize]
public sealed class DeliveryReceiptController(IPedidoQueueRepository repository) : ControllerBase
{
    [HttpGet("{operation:guid}")]
    public async Task<IActionResult> Receipt(Guid operation,CancellationToken ct)
    {
        Response.Headers.CacheControl="no-store";
        var owner=HttpContext.GetUserId(); if(owner is not >0) return Unauthorized();
        var receipt=await repository.GetDeliveryReceiptAsync(owner.Value,operation,ct);
        return receipt==null?NotFound(ApiResponse<object>.Fail("Conclusão ainda não registrada.","COMPLETION_NOT_FOUND")):Ok(ApiResponse<DeliveryReceipt>.Ok(receipt));
    }
    [HttpGet("{operation:guid}/proof")]
    public async Task<IActionResult> Proof(Guid operation,CancellationToken ct)
    {
        Response.Headers.CacheControl="no-store";
        var owner=HttpContext.GetUserId(); if(owner is not >0) return Unauthorized();
        var proof=await repository.GetDeliveryProofAsync(owner.Value,operation,ct);
        return proof==null?NotFound(ApiResponse<object>.Fail("Comprovante indisponível.")):Ok(ApiResponse<string>.Ok(proof));
    }
}
