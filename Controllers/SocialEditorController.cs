using System.Text.Json;
using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Social.Arquivos;
using APIBack.Social.Editor;
using APIBack.Social.Nucleo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace APIBack.Controllers;

/// <summary>
/// Editor do Zippy Social (R01): cortes, receitas versionadas, arquivos pequenos no R2 e o vídeo de exemplo.
/// Toda rota exige o módulo Social ligado na loja (<c>MODULO_INATIVO</c>) e a permissão da ação.
/// </summary>
[ApiController]
[Route("api/social")]
[RequireModule(SocialPermissoes.Modulo)]
public sealed class SocialEditorController(ISocialEditorService editor, ISocialArquivosService arquivos, IHostEnvironment ambiente)
    : ControllerBase
{
    // Folga sobre os limites do conteúdo (1 MB de dados, 512 KB de receita) para o envelope do pedido.
    private const long CorpoDados = EditorRegras.LimiteDadosBytes + 64 * 1024;
    private const long CorpoReceita = EditorRegras.LimiteReceitaBytes + 64 * 1024;

    [HttpGet("cortes")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Visualizar)]
    public Task<IActionResult> Cortes([FromQuery] string? ids) =>
        Run((loja, usuario, ct) => editor.CortesAsync(loja, usuario, EditorRegras.Ids(ids), ct));

    [HttpPut("cortes/{id:long}/dados")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Editar)]
    [RequestSizeLimit(CorpoDados)]
    public Task<IActionResult> SalvarDados(long id, [FromBody] JsonElement dados) =>
        Run(async (loja, _, ct) => { await editor.SalvarDadosAsync(loja, id, dados, ct); return new { ok = true }; });

    [HttpGet("cortes/{id:long}/receita")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Visualizar)]
    public Task<IActionResult> Receita(long id) => Run((loja, _, ct) => editor.ReceitaAsync(loja, id, ct));

    [HttpPut("cortes/{id:long}/receita")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Editar)]
    [RequestSizeLimit(CorpoReceita)]
    public Task<IActionResult> SalvarReceita(long id, [FromBody] SalvarReceitaRequest req) =>
        Run((loja, usuario, ct) => editor.SalvarReceitaAsync(loja, usuario, id, req, ct));

    [HttpGet("cortes/{id:long}/receita/historico")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Visualizar)]
    public Task<IActionResult> Historico(long id) => Run((loja, _, ct) => editor.HistoricoAsync(loja, id, ct));

    [HttpGet("cortes/{id:long}/receita/historico/{versao:int}")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Visualizar)]
    public Task<IActionResult> Versao(long id, int versao) => Run((loja, _, ct) => editor.VersaoAsync(loja, id, versao, ct));

    [HttpPost("arquivos")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Editar)]
    public Task<IActionResult> NovoArquivo([FromBody] NovoArquivoRequest req) =>
        Run((loja, usuario, ct) => arquivos.CriarAsync(loja, usuario, req, ct));

    [HttpPost("arquivos/{id:guid}/confirmar")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Editar)]
    public Task<IActionResult> Confirmar(Guid id) => Run((loja, _, ct) => arquivos.ConfirmarAsync(loja, id, ct));

    [HttpGet("arquivos/{id:guid}")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Visualizar)]
    public Task<IActionResult> Arquivo(Guid id) => Run((loja, _, ct) => arquivos.ObterAsync(loja, id, ct));

    [HttpDelete("arquivos/{id:guid}")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Editar)]
    public Task<IActionResult> ApagarArquivo(Guid id) =>
        Run(async (loja, usuario, ct) => { await arquivos.ApagarAsync(loja, usuario, id, ct); return new { ok = true }; });

    [HttpPost("videos/exemplo")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Editar)]
    public Task<IActionResult> Exemplo() => ambiente.IsDevelopment()
        ? Run((loja, usuario, ct) => editor.CriarExemploAsync(loja, usuario, ct))
        // Cortes de exemplo são só para desenvolvimento: em produção a rota responde 404 (C01).
        : Task.FromResult<IActionResult>(NotFound(ApiResponse<object>.Fail("Não encontrado.", "NAO_ENCONTRADO")));

    [HttpDelete("videos/{id:long}")]
    [RequirePermission(SocialPermissoes.Modulo, SocialPermissoes.Editar)]
    public Task<IActionResult> ApagarVideo(long id) =>
        Run(async (loja, _, ct) => { await editor.ApagarVideoAsync(loja, id, ct); return new { ok = true }; });

    private async Task<IActionResult> Run<T>(Func<Guid, int, CancellationToken, Task<T>> executar)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetEstabelecimentoId() is not Guid loja || HttpContext.GetUserId() is not int usuario)
            return Unauthorized(ApiResponse<object>.Fail("Entre de novo.", "NAO_AUTENTICADO"));
        try
        {
            return Ok(ApiResponse<object>.Ok((await executar(loja, usuario, HttpContext.RequestAborted))!));
        }
        catch (SocialException ex)
        {
            return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo, ex.Detalhes));
        }
    }
}
