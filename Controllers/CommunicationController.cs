using APIBack.Attributes;
using APIBack.DTOs.Atendimento;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace APIBack.Controllers;

public abstract class CommunicationControllerBase(CommunicationService service) : ControllerBase
{
    protected abstract bool Mobile { get; }
    private ChatActor Actor => new(HttpContext.GetEstabelecimentoId() ?? Guid.Empty, HttpContext.GetUserId() ?? 0,
        Mobile ? HttpContext.GetJwtPayload().MotoboyId : null);
    protected async Task<IActionResult> Run<T>(Func<ChatActor, Task<T>> work)
    {
        try { return Ok(ApiResponse<T>.Ok(await work(Actor))); }
        catch (DeliveryDomainException ex) { return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code)); }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
        { return StatusCode(503, ApiResponse<object>.Fail("Comunicacao ainda nao habilitada neste ambiente.", "MIGRATION_PENDING")); }
    }
    protected Task<IActionResult> List(string channel, int? target, long? before, string? search, int limit, int? pedidoId) => Run(a => service.ListAsync(a, channel, target, before, search, limit, pedidoId));
    protected Task<IActionResult> Context(string channel,int? target,Guid id,int? pedidoId) => Run(a=>service.ContextAsync(a,channel,target,id,pedidoId));
    protected Task<IActionResult> Send(string channel, int? target, SendCommunicationRequest request) => Run(a => service.SendAsync(a, channel, target, request));
    protected Task<IActionResult> Read(string channel, int? target, CommunicationReadRequest request) => Run(async a => { await service.ReadAsync(a, channel, target, request.Through); return new { }; });
    protected Task<IActionResult> React(string channel, int? target, Guid id, CommunicationReactionRequest request) => Run(async a => { await service.ReactAsync(a, channel, target, id, request.Reaction); return new { }; });
    protected Task<IActionResult> Upload(string channel, int? target, IFormFile file) => Run(a => service.UploadAsync(a, channel, target, file, HttpContext.RequestAborted));
    protected async Task<IActionResult> Download(Guid id)
    {
        try
        {
            var file = await service.FileAsync(Actor, id, HttpContext.RequestAborted);
            Response.Headers.CacheControl = "private, no-store";
            Response.Headers.XContentTypeOptions = "nosniff";
            return File(file.Content, file.ContentType, enableRangeProcessing: true);
        }
        catch (DeliveryDomainException ex) { return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code)); }
    }
    protected Task<IActionResult> Notifications() => Run(a => service.NotificationsAsync(a));
    protected Task<IActionResult> Dismiss(Guid id) => Run(async a => { await service.DismissAsync(a, id); return new { }; });
}

[ApiController, Route("api/v2/motoboys/me/session/chat"), RequireOperationalSession]
public sealed class MobileCommunicationController(CommunicationService service) : CommunicationControllerBase(service)
{
    protected override bool Mobile => true;
    [HttpGet("{channel}/messages")]
    public Task<IActionResult> Messages(string channel, int? target, long? before, string? search, int limit = 50, int? pedidoId = null) => List(channel,target,before,search,limit,pedidoId);
    [HttpGet("{channel}/messages/{id:guid}/context")]
    public Task<IActionResult> MessageContext(string channel,int? target,Guid id,int? pedidoId=null)=>Context(channel,target,id,pedidoId);
    [HttpPost("{channel}/messages")]
    public Task<IActionResult> Message(string channel, int? target, SendCommunicationRequest request) => Send(channel,target,request);
    [HttpPost("{channel}/read")]
    public Task<IActionResult> MarkRead(string channel, int? target, CommunicationReadRequest request) => Read(channel,target,request);
    [HttpPut("{channel}/messages/{id:guid}/reaction")]
    public Task<IActionResult> Reaction(string channel, int? target, Guid id, CommunicationReactionRequest request) => React(channel,target,id,request);
    [HttpPost("{channel}/attachments"), RequestSizeLimit(11_000_000)]
    public Task<IActionResult> Attachment(string channel, int? target, IFormFile file) => Upload(channel,target,file);
    [HttpGet("attachments/{id:guid}")]
    public Task<IActionResult> File(Guid id) => Download(id);
    [HttpGet("notifications")]
    public Task<IActionResult> Notices() => Notifications();
    [HttpPost("notifications/{id:guid}/read")]
    public Task<IActionResult> ReadNotice(Guid id) => Dismiss(id);
}

[ApiController, Route("api/v2/atendimento/chat")]
public sealed class AdminCommunicationController(CommunicationService service) : CommunicationControllerBase(service)
{
    protected override bool Mobile => false;
    [HttpGet("{channel}/messages"), RequirePermission("Delivery","visualizar")]
    public Task<IActionResult> Messages(string channel, int? target, long? before, string? search, int limit = 50, int? pedidoId = null) => List(channel,target,before,search,limit,pedidoId);
    [HttpPost("{channel}/messages"), RequirePermission("Delivery","atribuir_motoboy")]
    public Task<IActionResult> Message(string channel, int? target, SendCommunicationRequest request) => Send(channel,target,request);
    [HttpPost("{channel}/read"), RequirePermission("Delivery","visualizar")]
    public Task<IActionResult> MarkRead(string channel, int? target, CommunicationReadRequest request) => Read(channel,target,request);
    [HttpPut("{channel}/messages/{id:guid}/reaction"), RequirePermission("Delivery","atribuir_motoboy")]
    public Task<IActionResult> Reaction(string channel, int? target, Guid id, CommunicationReactionRequest request) => React(channel,target,id,request);
    [HttpPost("{channel}/attachments"), RequirePermission("Delivery","atribuir_motoboy"), RequestSizeLimit(11_000_000)]
    public Task<IActionResult> Attachment(string channel, int? target, IFormFile file) => Upload(channel,target,file);
    [HttpGet("attachments/{id:guid}"), RequirePermission("Delivery","visualizar")]
    public Task<IActionResult> File(Guid id) => Download(id);
}
