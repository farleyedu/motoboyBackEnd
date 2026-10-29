using APIBack.Acontecimentos;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.GestaoLogica.Documentos;
using APIBack.Security;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

/// <summary>
/// Documentos de compra (CONTRATOS, seção "Lógica"). Lê o XML ou a foto e devolve o que foi lido, com avisos e duplicidades.
/// Não cria a compra: o financeiro e o estoque só mudam nos passos seguintes, com uma pessoa confirmando.
/// </summary>
[ApiController]
[Route("api/gestao/compras/documentos")]
public sealed class GestaoDocumentosController(IDocumentosService service) : ControllerBase
{
    private const long LimiteBytes = 10 * 1024 * 1024;

    [HttpPost("xml")]
    [RequestSizeLimit(LimiteBytes)]
    public Task<IActionResult> Xml(IFormFile arquivo, CancellationToken ct) => Run(async (loja, usuario) =>
        await service.RegistrarXml(loja, usuario, await Ler(arquivo, ct), ct));

    [HttpPost("foto")]
    [RequestSizeLimit(LimiteBytes)]
    public Task<IActionResult> Foto(IFormFile arquivo, CancellationToken ct) => Run(async (loja, usuario) =>
    {
        var tipo = arquivo?.ContentType?.ToLowerInvariant() ?? "";
        if (tipo is not ("image/jpeg" or "image/png" or "image/webp" or "image/gif"))
            throw new GestaoException("CAMPO_INVALIDO", "Envie uma foto em JPG, PNG, WEBP ou GIF.", 422);
        return await service.RegistrarFoto(loja, usuario, await Ler(arquivo!, ct), tipo, ct);
    });

    [HttpGet("{id:guid}")]
    public Task<IActionResult> Obter(Guid id, CancellationToken ct) => Run(async (loja, _) =>
        await service.Obter(loja, id, ct) ?? throw new GestaoException("NAO_ENCONTRADO", "Documento não encontrado.", 404));

    /// <summary>Arquivo original (foto/XML). Documentos lançados antes da Integração 6B não têm arquivo guardado: 404.</summary>
    [HttpGet("{id:guid}/arquivo")]
    public async Task<IActionResult> Arquivo(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, max-age=3600";
        if (HttpContext.GetEstabelecimentoId() is not Guid loja || HttpContext.GetUserId() is null) return Unauthorized();
        var acesso = GestaoPapeis.Resolver(HttpContext.TemPermissao, HttpContext.IsSuperAdmin());
        if (acesso.Papel != GestaoPapeis.Gerente)
            return StatusCode(403, ApiResponse<object>.Fail("Só o gerente vê os documentos de compra.", "SEM_PERMISSAO"));
        return await service.Arquivo(loja, id, ct) is { } a
            ? File(a.Conteudo, a.MediaType)
            : NotFound(ApiResponse<object>.Fail("Este documento não tem o arquivo original guardado.", "NAO_ENCONTRADO"));
    }

    private static async Task<byte[]> Ler(IFormFile? arquivo, CancellationToken ct)
    {
        if (arquivo is null || arquivo.Length == 0) throw new GestaoException("CAMPO_INVALIDO", "Envie o arquivo.", 422);
        if (arquivo.Length > LimiteBytes) throw new GestaoException("CAMPO_INVALIDO", "O arquivo passa de 10 MB.", 422);
        using var ms = new MemoryStream();
        await arquivo.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    // Compras é do gerente que lança no Financeiro (o operador de caixa não vê Compras).
    private async Task<IActionResult> Run(Func<Guid, int, Task<object>> acao)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetEstabelecimentoId() is not Guid loja || HttpContext.GetUserId() is not int usuario) return Unauthorized();
        var acesso = GestaoPapeis.Resolver(HttpContext.TemPermissao, HttpContext.IsSuperAdmin());
        try
        {
            if (acesso.Papel != GestaoPapeis.Gerente || !acesso.PodeLancar)
                throw new GestaoException("SEM_PERMISSAO", "Só o gerente lança compras.", 403);
            return Ok(ApiResponse<object>.Ok(await acao(loja, usuario)));
        }
        catch (GestaoException ex) { return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo)); }
    }
}
