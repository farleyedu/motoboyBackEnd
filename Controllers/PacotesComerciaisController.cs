using System;
using System.Threading;
using System.Threading.Tasks;
using APIBack.DTOs.Common;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

/// <summary>Catalogo da Gestao: planos (criar, editar, arquivar, excluir) e avulsos. So super admin.</summary>
[ApiController]
public sealed class PacotesComerciaisController(IPacotesComerciaisService service) : GestaoControllerBase
{
    private async Task<IActionResult> Run<T>(Func<int, Task<T>> action)
    {
        if (!CurrentUserId.HasValue) return UnauthorizedResponse();
        if (!CurrentIsSuperAdmin) return ForbiddenResponse();
        try { return Ok(ApiResponse<T>.Ok(await action(CurrentUserId.Value))); }
        catch (SituacaoComercialException ex) { return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Code)); }
    }

    [HttpGet("api/gestao/comercial/pacotes")]
    public Task<IActionResult> Listar(CancellationToken ct) => Run(_ => service.ListarAsync(ct));

    [HttpPost("api/gestao/comercial/pacotes")]
    public Task<IActionResult> Criar([FromBody] CriarPacoteComercialRequest request, CancellationToken ct) => Run(u => service.CriarAsync(request, u, ct));

    [HttpPut("api/gestao/comercial/pacotes/{chave}")]
    public Task<IActionResult> Atualizar(string chave, [FromBody] EditarPacoteComercialRequest request, CancellationToken ct) => Run(u => service.AtualizarAsync(chave, request, u, ct));

    [HttpPost("api/gestao/comercial/pacotes/{chave}/arquivar")]
    public Task<IActionResult> Arquivar(string chave, [FromQuery] bool arquivado, CancellationToken ct) => Run(u => service.ArquivarAsync(chave, arquivado, u, ct));

    [HttpDelete("api/gestao/comercial/pacotes/{chave}")]
    public Task<IActionResult> Excluir(string chave, CancellationToken ct) => Run(async _ => { await service.ExcluirAsync(chave, ct); return (object)true; });

    [HttpGet("api/gestao/comercial/avulsos")]
    public Task<IActionResult> Avulsos(CancellationToken ct) => Run(_ => service.ListarAvulsosAsync(ct));

    [HttpPost("api/gestao/comercial/avulsos")]
    public Task<IActionResult> CriarAvulso([FromBody] SalvarProdutoAvulsoRequest request, CancellationToken ct) => Run(_ => service.SalvarAvulsoAsync(null, request, ct));

    [HttpPut("api/gestao/comercial/avulsos/{id:long}")]
    public Task<IActionResult> SalvarAvulso(long id, [FromBody] SalvarProdutoAvulsoRequest request, CancellationToken ct) => Run(_ => service.SalvarAvulsoAsync(id, request, ct));

    [HttpDelete("api/gestao/comercial/avulsos/{id:long}")]
    public Task<IActionResult> ExcluirAvulso(long id, CancellationToken ct) => Run(async _ => { await service.ExcluirAvulsoAsync(id, ct); return (object)true; });
}
