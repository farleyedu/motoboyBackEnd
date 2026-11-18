using System;
using System.Threading.Tasks;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Integrations.NotaFiscal.Nucleo;
using APIBack.Payments.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace APIBack.Controllers;

/// <summary>
/// Nota fiscal, nucleo (docs/nota-fiscal/CONTRATO.md): conexao com o provedor, regras de emissao e dados fiscais dos produtos.
/// So dono e gerente. A chave do provedor entra por PUT /conexao e nunca volta em resposta, log ou erro.
/// </summary>
[ApiController, Route("api/v2/nota-fiscal")]
public sealed class NotaFiscalConexaoController : ControllerBase
{
    private readonly INotaFiscalNucleoService _servico;
    private readonly ILogger<NotaFiscalConexaoController> _log;

    public NotaFiscalConexaoController(INotaFiscalNucleoService servico, ILogger<NotaFiscalConexaoController> log)
    {
        _servico = servico; _log = log;
    }

    [HttpGet("provedores")]
    public Task<IActionResult> Provedores() => Executar(() => Task.FromResult(_servico.CatalogoProvedores()));

    [HttpGet("conexao")]
    public Task<IActionResult> ObterConexao() => Executar(() => _servico.ObterConexaoAsync(Loja(), Ct));

    [HttpPut("conexao")]
    public Task<IActionResult> SalvarConexao([FromBody] SalvarConexaoRequest request) =>
        Executar(() => _servico.SalvarConexaoAsync(Loja(), Usuario(), request, Ct));

    [HttpDelete("conexao")]
    public Task<IActionResult> RemoverConexao() => Executar(() => _servico.RemoverConexaoAsync(Loja(), Usuario(), Ct));

    [HttpPost("conexao/testar")]
    public Task<IActionResult> Testar() => Executar(() => _servico.TestarAsync(Loja(), Ct));

    [HttpGet("regras")]
    public Task<IActionResult> ObterRegras() => Executar(() => _servico.ObterRegrasAsync(Loja(), Ct));

    [HttpPut("regras")]
    public Task<IActionResult> SalvarRegras([FromBody] RegrasDto regras) =>
        Executar(() => _servico.SalvarRegrasAsync(Loja(), Usuario(), regras, Ct));

    [HttpGet("produtos-fiscais")]
    public Task<IActionResult> ListarProdutos([FromQuery] bool pendentes = false, [FromQuery] string? busca = null) =>
        Executar(() => _servico.ListarProdutosAsync(Loja(), pendentes, busca, Ct));

    [HttpPut("produtos-fiscais/{produtoId:guid}")]
    public Task<IActionResult> SalvarProduto(Guid produtoId, [FromBody] ProdutoFiscalRequest valores) =>
        Executar(() => _servico.SalvarProdutoAsync(Loja(), produtoId, valores, Ct));

    [HttpPut("produtos-fiscais/lote")]
    public Task<IActionResult> SalvarLote([FromBody] ProdutoFiscalLoteRequest lote) =>
        Executar(() => _servico.SalvarLoteAsync(Loja(), lote, Ct));

    // ---------- internos ----------

    private System.Threading.CancellationToken Ct => HttpContext.RequestAborted;

    private Guid Loja()
    {
        Autorizar();
        return HttpContext.GetEstabelecimentoId() ?? throw NotaFiscalException.SemPermissao("Selecione uma loja.");
    }

    private int Usuario()
    {
        Autorizar();
        return HttpContext.GetUserId() ?? throw new NotaFiscalException("NF_NAO_AUTENTICADO", 401, "Usuario nao autenticado.");
    }

    private void Autorizar()
    {
        if (!ConexaoMercadoPagoAcesso.PodeGerenciar(HttpContext))
            throw NotaFiscalException.SemPermissao("So o dono ou o gerente da loja pode mexer na nota fiscal.");
    }

    private async Task<IActionResult> Executar<T>(Func<Task<T>> operacao)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            return Ok(ApiResponse<T>.Ok(await operacao()));
        }
        catch (NotaFiscalException ex)
        {
            return StatusCode(ex.Http, ApiResponse<object>.Fail(ex.Message, ex.Codigo));
        }
        catch (Exception ex)
        {
            // So o tipo da excecao: a mensagem pode carregar dado do provedor.
            _log.LogError("[nf.api] ev=erro_inesperado rota={Rota} tipo={Tipo}", Request.Path.Value, ex.GetType().Name);
            return StatusCode(500, ApiResponse<object>.Fail("Nao foi possivel concluir a operacao de nota fiscal."));
        }
    }
}
