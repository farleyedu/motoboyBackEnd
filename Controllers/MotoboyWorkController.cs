using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Delivery;
using APIBack.Extensions;
using APIBack.Model.Auth;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

[ApiController,Route("api/motoboys/me/work"),APIBack.Attributes.Authorize]
public sealed class MotoboyWorkController(MotoboyWorkService service) : ControllerBase
{
    private int? Owner => HttpContext.Items["JwtPayload"] is JwtPayload p && !p.MotoboySessionId.HasValue ? HttpContext.GetUserId() : null;
    [HttpGet]
    public Task<IActionResult> Read(Guid store,DateTimeOffset from,DateTimeOffset to,CancellationToken ct) => Run(store,async(r,u)=>await service.Read(store,r,from,to,ct),ct);
    [HttpPost("settlements/{id:guid}/actions")]
    public Task<IActionResult> Action(Guid id,Guid store,RiderSettlementAction request,CancellationToken ct) => Run(store,async(r,u)=>await service.Action(store,r,u,id,request,true,ct),ct);
    [HttpPost("support")]
    public Task<IActionResult> Support(Guid store,RiderSupportRequest request,CancellationToken ct) => Run(store,async(r,u)=>await service.Support(store,r,request,ct),ct);
    private async Task<IActionResult> Run(Guid store,Func<int,int,Task<object>> action,CancellationToken ct)
    {
        Response.Headers.CacheControl="no-store";
        if(Owner is not >0) return Unauthorized(ApiResponse<object>.Fail("Entre na sua conta para consultar ganhos e acertos."));
        try { return Ok(ApiResponse<object>.Ok(await action(await service.OwnRider(Owner.Value,store,ct),Owner.Value))); }
        catch(DeliveryDomainException ex){return StatusCode(ex.StatusCode,ApiResponse<object>.Fail(ex.Message,ex.Code,ex.Details));}
        catch(ArgumentException ex){return BadRequest(ApiResponse<object>.Fail(ex.Message,"VALIDATION_ERROR"));}
    }
}

[ApiController,Route("api/v2/delivery/rider-work"),APIBack.Attributes.RequireModule("Remuneracao")]
public sealed class RiderWorkAdminController(MotoboyWorkService service) : ControllerBase
{
    /// <summary>Remuneração PADRÃO da loja (vale para todo motoboy sem exceção, inclusive os novos). Existe mesmo sem nenhum motoboy vinculado.</summary>
    [HttpGet("default"),RequirePermission("Delivery","gestao_motoboy")]
    public Task<IActionResult> Default(CancellationToken ct) => Run(async(s,u)=>await service.ReadDefault(s,ct));
    /// <summary>Nova versão do padrão (modo + valor, histórico versionado, idempotente por operationId). Só afeta quem não tem exceção.</summary>
    [HttpPut("default"),RequirePermission("Delivery","gestao_motoboy")]
    public Task<IActionResult> SaveDefault(RiderPayPlanRequest request,CancellationToken ct) => Run(async(s,u)=>await service.SaveDefault(s,u,request,ct));
    /// <summary>"Voltar ao padrão": o motoboy deixa a exceção individual e segue o padrão da loja. Fica no histórico; nada é apagado.</summary>
    [HttpDelete("{rider:int}/override"),RequirePermission("Delivery","gestao_motoboy")]
    public Task<IActionResult> ResetOverride(int rider,Guid operationId,Guid? expectedPlanId,CancellationToken ct) => Run(async(s,u)=>await service.ResetToDefault(s,rider,u,operationId,expectedPlanId,ct));
    /// <summary>Resumo da loja: uma linha por motoboy vinculado (plano, saldo, recebimentos pendentes e totais do período). Sem from/to: últimos 30 dias.</summary>
    [HttpGet("resumo"),RequirePermission("Delivery","gestao_motoboy")]
    public Task<IActionResult> Summary(DateTimeOffset? from,DateTimeOffset? to,CancellationToken ct) => Run(async(s,u)=>await service.Summary(s,from,to,ct));
    /// <summary>Mesma regra de remuneração para vários motoboys numa chamada; devolve o resultado por motoboy.</summary>
    [HttpPost("plans"),RequirePermission("Delivery","gestao_motoboy")]
    public Task<IActionResult> Plans(RiderPayPlanBatchRequest request,CancellationToken ct) => Run(async(s,u)=>await service.SavePlans(s,u,request,ct));
    [HttpGet("{rider:int}"),RequirePermission("Delivery","gestao_motoboy")]
    public Task<IActionResult> Read(int rider,DateTimeOffset from,DateTimeOffset to,CancellationToken ct) => Run((s,u)=>service.Read(s,rider,from,to,ct));
    [HttpPost("{rider:int}/plans"),RequirePermission("Delivery","gestao_motoboy")]
    public Task<IActionResult> Plan(int rider,RiderPayPlanRequest request,CancellationToken ct) => Run(async(s,u)=>await service.SavePlan(s,rider,u,request,ct));
    [HttpPost("{rider:int}/periods"),RequirePermission("Delivery","gestao_motoboy")]
    public Task<IActionResult> Period(int rider,RiderPeriodRequest request,CancellationToken ct) => Run(async(s,u)=>await service.AddPeriod(s,rider,u,request,ct));
    [HttpPost("{rider:int}/backfill"),RequirePermission("Delivery","gestao_motoboy")]
    public Task<IActionResult> Backfill(int rider,CancellationToken ct) => Run(async(s,u)=>await service.Backfill(s,rider,u,ct));
    [HttpPost("{rider:int}/settlements"),RequirePermission("Delivery","gestao_motoboy")]
    public Task<IActionResult> Settlement(int rider,RiderSettlementRequest request,CancellationToken ct) => Run(async(s,u)=>await service.CreateSettlement(s,rider,u,request.OperationId,ct));
    [HttpPost("{rider:int}/settlements/{id:guid}/actions"),RequirePermission("Delivery","gestao_motoboy")]
    public Task<IActionResult> Action(int rider,Guid id,RiderSettlementAction request,CancellationToken ct) => Run(async(s,u)=>await service.Action(s,rider,u,id,request,false,ct));
    private async Task<IActionResult> Run(Func<Guid,int,Task<object>> action)
    {
        Response.Headers.CacheControl="no-store";
        if(HttpContext.GetEstabelecimentoId() is not Guid store || HttpContext.GetUserId() is not int user) return Unauthorized();
        try { return Ok(ApiResponse<object>.Ok(await action(store,user))); }
        catch(DeliveryDomainException ex){return StatusCode(ex.StatusCode,ApiResponse<object>.Fail(ex.Message,ex.Code,ex.Details));}
        catch(ArgumentException ex){return BadRequest(ApiResponse<object>.Fail(ex.Message,"VALIDATION_ERROR"));}
        catch(Npgsql.PostgresException ex) when(ex.SqlState==Npgsql.PostgresErrorCodes.UniqueViolation){return Conflict(ApiResponse<object>.Fail("Identificação reutilizada. Atualize e confira o registro.","WORK_CONFLICT"));}
    }
}
