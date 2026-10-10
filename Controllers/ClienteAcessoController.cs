using APIBack.DTOs.Clientes;
using APIBack.DTOs.Common;
using APIBack.Repository.Interface;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace APIBack.Controllers;

[ApiController, AllowAnonymous, Route("api/cardapio/web/cliente")]
public sealed class ClienteAcessoController(IClienteAcessoService acesso, IClienteEnderecoRepository enderecos, IMemoryCache cache) : ControllerBase
{
    private string? Token => Request.Headers["X-Cliente-Sessao"].FirstOrDefault();

    [HttpPost("autenticar")]
    public Task<IActionResult> Authenticate(ClienteAutenticarRequest body) => Execute(async () =>
    {
        var key = "cliente-acesso:" + (HttpContext.Connection.RemoteIpAddress?.ToString() ?? "local");
        var count = cache.GetOrCreate(key, entry => { entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10); return new int[1]; })!;
        if (Interlocked.Increment(ref count[0]) > 180)
            throw new DeliveryDomainException(429, "RATE_LIMIT", "Muitas tentativas. Aguarde alguns minutos.");
        return await acesso.AuthenticateAsync(body.EstabelecimentoId, body.Telefone);
    });

    [HttpGet("sessao")]
    public Task<IActionResult> Profile(Guid estabelecimentoId) => Execute(() => acesso.ProfileAsync(estabelecimentoId, Token));

    [HttpDelete("sessao")]
    public Task<IActionResult> Logout(Guid estabelecimentoId) => Execute(async () => { await acesso.LogoutAsync(estabelecimentoId, Token); return new { saiu = true }; });

    [HttpPost("enderecos")]
    public Task<IActionResult> Create(Guid estabelecimentoId, ClienteEnderecoRequest body) => Save(estabelecimentoId, null, body);

    [HttpPut("enderecos/{id:guid}")]
    public Task<IActionResult> Update(Guid estabelecimentoId, Guid id, ClienteEnderecoRequest body) => Save(estabelecimentoId, id, body);

    private Task<IActionResult> Save(Guid est, Guid? id, ClienteEnderecoRequest body) => Execute(async () =>
    {
        var session = await acesso.RequireAsync(est, Token);
        return await enderecos.SaveAsync(est, session.ClienteId, id, body);
    });

    [HttpDelete("enderecos/{id:guid}")]
    public Task<IActionResult> Delete(Guid estabelecimentoId, Guid id) => Execute(async () =>
    {
        var session = await acesso.RequireAsync(estabelecimentoId, Token);
        await enderecos.DeleteAsync(estabelecimentoId, session.ClienteId, id);
        return new { id };
    });

    private async Task<IActionResult> Execute<T>(Func<Task<T>> action)
    {
        try { return Ok(ApiResponse<T>.Ok(await action())); }
        catch (DeliveryDomainException ex) { return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code)); }
    }
}
