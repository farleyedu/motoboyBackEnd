using APIBack.Attributes;
using APIBack.Extensions;
using APIBack.Printing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;

namespace APIBack.Controllers;

[ApiController,Route("api/v2/delivery/impressao")]
public sealed class PrintController(PrintStore store,IConfiguration configuration,IHostEnvironment environment):ControllerBase, Microsoft.AspNetCore.Mvc.Filters.IExceptionFilter
{
 public void OnException(Microsoft.AspNetCore.Mvc.Filters.ExceptionContext context)
 {
  if(context.Exception is APIBack.Service.DeliveryDomainException error)
  {context.Result=new ObjectResult(new{success=false,error=error.Message}){StatusCode=error.StatusCode};context.ExceptionHandled=true;}
 }
 private Guid Shop=>HttpContext.GetEstabelecimentoId()??throw PrintStore.Error("Selecione uma loja.",401);
 private int Actor=>HttpContext.GetUserId()??throw PrintStore.Error("Entre novamente.",401);
 private void CanConfigure()
 {
  var role=HttpContext.GetTipoAcesso();
  if(!HttpContext.IsSuperAdmin() && role is not("dono" or "gerente_empresa" or "gerente_estabelecimento") && !HttpContext.TemPermissao("Delivery","configurar_impressao"))throw PrintStore.Error("Somente gerente, proprietário ou pessoa autorizada pode configurar impressão.",403);
 }
 private IActionResult Data(object? value)=>Ok(new{success=true,data=value});
 [HttpGet("configuracao"),RequirePermission("Delivery","visualizar")]
 public async Task<IActionResult> Config()=>Data(await store.Config(Shop,true));
 [HttpPut("configuracao"),RequirePermission("Delivery","visualizar")]
 public async Task<IActionResult> Save(PrintConfig input){CanConfigure();return Data(await store.Save(Shop,input));}
 // The explicit capability is catalogued for delegation in Equipe.
 [HttpGet("permissao"),RequirePermission("Delivery","configurar_impressao")]
 public IActionResult Permission()=>Data(true);
 [HttpGet("loja"),RequirePermission("Delivery","visualizar")]
 public async Task<IActionResult> Business()=>Data(await store.Shop(Shop));
 [HttpGet("estacoes"),RequirePermission("Delivery","visualizar")]
 public async Task<IActionResult> Stations()=>Data(await store.Stations(Shop));
 [HttpPost("estacoes"),RequirePermission("Delivery","visualizar")]
 public async Task<IActionResult> Add(StationInput input){CanConfigure();return Data(await store.CreateStation(Shop,Actor,input));}
 [HttpPut("estacoes/{id:guid}"),RequirePermission("Delivery","visualizar")]
 public async Task<IActionResult> Set(Guid id,[FromQuery]bool? ativa,[FromQuery]string? nome){CanConfigure();if(nome!=null)await store.RenameStation(Shop,id,nome);if(ativa.HasValue)await store.SetStation(Shop,id,ativa.Value);return Data(true);}
 [HttpDelete("estacoes/{id:guid}"),RequirePermission("Delivery","visualizar")]
 public async Task<IActionResult> Remove(Guid id){CanConfigure();await store.SetStation(Shop,id,null);return Data(true);}
 [HttpPost("estacoes/{id:guid}/pareamento"),RequirePermission("Delivery","visualizar")]
 public async Task<IActionResult> Code(Guid id){CanConfigure();return Data(await store.PairCode(Shop,Actor,id));}
 [HttpPost("estacoes/{id:guid}/sinal"),RequirePermission("Delivery","visualizar")]
 public async Task<IActionResult> Signal(Guid id,Heartbeat input)=>Data(new{dona=await store.Signal(Shop,id,Actor,input)});
 [HttpGet("estacoes/{id:guid}/jobs"),RequirePermission("Delivery","visualizar")]
 public async Task<IActionResult> Pending(Guid id,[FromQuery]Guid sessaoId)=>Data(await store.Pending(Shop,id,sessaoId,false));
 [HttpPost("estacoes/{id:guid}/jobs/{job:guid}/receber"),RequirePermission("Delivery","visualizar")]
 public async Task<IActionResult> Receive(Guid id,Guid job,[FromQuery]Guid sessaoId)=>Data(new{recebido=await store.Receive(Shop,id,job,sessaoId,false)});
 [HttpPost("estacoes/{id:guid}/jobs/{job:guid}/resultado"),RequirePermission("Delivery","visualizar")]
 public async Task<IActionResult> Result(Guid id,Guid job,PrintResult input){await store.Result(Shop,id,job,input,false);return Data(true);}
 [HttpGet("rascunho"),RequirePermission("Delivery","visualizar")]
 public async Task<IActionResult> Draft()=>Data(await store.Draft(Shop,Actor));
 [HttpPut("rascunho"),RequirePermission("Delivery","visualizar")]
 public async Task<IActionResult> SaveDraft(PrintDraft input){CanConfigure();await store.SaveDraft(Shop,Actor,input);return Data(true);}
 [HttpDelete("rascunho"),RequirePermission("Delivery","visualizar")]
 public async Task<IActionResult> ClearDraft(){CanConfigure();await store.SaveDraft(Shop,Actor,null);return Data(true);}
 [HttpGet("jobs"),RequirePermission("Delivery","visualizar")]
 public async Task<IActionResult> Jobs([FromQuery]int? pedidoId)=>Data(await store.Jobs(Shop,pedidoId));
 [HttpGet("resumo"),RequirePermission("Delivery","visualizar")]
 public async Task<IActionResult> Summary()=>Data(await store.Summary(Shop));
 [HttpGet("contadores"),RequirePermission("Delivery","visualizar")]
 public async Task<IActionResult> Counts()=>Data(await store.Counts(Shop));
 [HttpPost("pedidos/{id:int}/imprimir"),RequirePermission("Delivery","visualizar")]
 public async Task<IActionResult> Manual(int id,[FromQuery]Guid? impressoraId){await store.Manual(Shop,Actor,id,impressoraId,false,Request.Headers["Idempotency-Key"].ToString());return Data(true);}
 [HttpDelete("impressoras/{id:guid}"),RequirePermission("Delivery","visualizar")]
 public async Task<IActionResult> RemovePrinter(Guid id){CanConfigure();await store.RemovePrinter(Shop,id);return Data(true);}
 [HttpPost("impressoras/{id:guid}/teste"),RequirePermission("Delivery","visualizar")]
 public async Task<IActionResult> Test(Guid id){CanConfigure();await store.Manual(Shop,Actor,null,id,true,Request.Headers["Idempotency-Key"].ToString());return Data(true);}
 [HttpPost("jobs/{id:guid}/{actionName}"),RequirePermission("Delivery","visualizar")]
 public async Task<IActionResult> Action(Guid id,string actionName){await store.JobAction(Shop,Actor,id,actionName,Request.Headers["Idempotency-Key"].ToString());return Data(true);}
 private async Task<AgentIdentity> Agent()=>await store.Agent(Request.Headers["X-Zippy-Station"].ToString())??throw PrintStore.Error("Estação não autorizada ou revogada.",401);
 [AllowAnonymous,HttpPost("agente/parear"),EnableRateLimiting("print-pairing")]
 public async Task<IActionResult> Pair(PairInput input)=>Data(await store.Pair(input));
 [AllowAnonymous,HttpPost("agente/sinal")]
 public async Task<IActionResult> AgentSignal(Heartbeat input){var a=await Agent();return Data(new{dona=await store.Signal(a.EstabelecimentoId,a.Id,0,input,true)});}
 [AllowAnonymous,HttpGet("agente/jobs/pendentes")]
 public async Task<IActionResult> AgentPending(){var a=await Agent();return Data(await store.Pending(a.EstabelecimentoId,a.Id,Guid.Empty,true));}
 [AllowAnonymous,HttpGet("agente/impressoras")]
 public async Task<IActionResult> AgentPrinters(){var a=await Agent();return Data((await store.Config(a.EstabelecimentoId)).Impressoras.Where(p=>p.EstacaoId==a.Id&&p.Ativa));}
 [AllowAnonymous,HttpPost("agente/jobs/{id:guid}/receber")]
 public async Task<IActionResult> AgentReceive(Guid id,[FromQuery]Guid sessaoId){var a=await Agent();return Data(new{recebido=await store.Receive(a.EstabelecimentoId,a.Id,id,sessaoId,true)});}
 [AllowAnonymous,HttpPost("agente/jobs/{id:guid}/resultado")]
 public async Task<IActionResult> AgentResult(Guid id,PrintResult input){var a=await Agent();await store.Result(a.EstabelecimentoId,a.Id,id,input,true);return Data(true);}
 [AllowAnonymous,HttpGet("agente/versao")]
 public IActionResult Version()
 {
  var path=Path.Combine(environment.ContentRootPath,"PrintAgent","ZippyPrintSetup.exe");
  if(!System.IO.File.Exists(path))return Data(new{disponivel=false});
  using var stream=System.IO.File.OpenRead(path);var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
  return Data(new{disponivel=true,versao=configuration["Printing:AgentVersion"]??"1.0.0",tamanho=new FileInfo(path).Length,sha256=hash,download="/api/v2/delivery/impressao/agente/instalador"});
 }
 [AllowAnonymous,HttpGet("agente/instalador")]
 public IActionResult Installer(){var path=Path.Combine(environment.ContentRootPath,"PrintAgent","ZippyPrintSetup.exe");return System.IO.File.Exists(path)?PhysicalFile(path,"application/octet-stream","ZippyPrintSetup.exe"):NotFound(new{success=false,error="Instalador ainda não disponível."});}
}
