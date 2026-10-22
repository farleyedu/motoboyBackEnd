using APIBack.Attributes;
using APIBack.DTOs.Cardapio;
using APIBack.DTOs.Common;
using APIBack.Repository;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

/// <summary>
/// Liga os itens dos pedidos iFood aos produtos do cardápio da loja (só exibição: foto, categoria, aviso de bag). Nunca altera
/// preço, quantidade ou total do pedido. A loja vem sempre do token; produto ou vínculo de outra loja responde 404.
/// </summary>
[ApiController]
public sealed class CardapioIfoodVinculoController(IfoodProdutoVinculoRepository repository) : EstabelecimentoScopedControllerBase
{
    [HttpGet("api/v2/cardapio/ifood/resumo")]
    [RequirePermission("Cardapio", "editar")]
    public Task<IActionResult> Resumo(CancellationToken ct) =>
        RunAsync(store => repository.ResumoAsync(store, ct));

    [HttpGet("api/v2/cardapio/ifood/pendentes")]
    [RequirePermission("Cardapio", "editar")]
    public Task<IActionResult> Pendentes([FromQuery] string? busca, [FromQuery] int pagina = 1, [FromQuery] int tamanho = 20, CancellationToken ct = default) =>
        RunAsync(store => repository.PendentesAsync(store, busca, pagina, tamanho, ct));

    [HttpGet("api/v2/cardapio/produtos/{produtoId:guid}/ifood")]
    [RequirePermission("Cardapio", "editar")]
    public Task<IActionResult> DoProduto(Guid produtoId, CancellationToken ct) =>
        RunAsync(store => repository.DoProdutoAsync(store, produtoId, ct));

    [HttpPost("api/v2/cardapio/produtos/{produtoId:guid}/ifood/vinculos")]
    [RequirePermission("Cardapio", "editar")]
    public Task<IActionResult> Vincular(Guid produtoId, [FromBody] IfoodVincularRequest? request, CancellationToken ct) =>
        request?.VinculoId is not { } vinculoId || vinculoId == Guid.Empty
            ? Task.FromResult<IActionResult>(BadRequest(ApiResponse<object>.Fail("Informe o item do iFood a ligar.", "IFOOD_VINCULO_OBRIGATORIO")))
            : RunAsync(store => repository.VincularAsync(store, CurrentUserId, produtoId, vinculoId, ct));

    [HttpDelete("api/v2/cardapio/ifood/vinculos/{vinculoId:guid}")]
    [RequirePermission("Cardapio", "editar")]
    public Task<IActionResult> Desfazer(Guid vinculoId, CancellationToken ct) =>
        RunAsync(store => repository.DesfazerAsync(store, CurrentUserId, vinculoId, ct));

    private async Task<IActionResult> RunAsync<T>(Func<Guid, Task<T>> action)
    {
        if (!TryResolveCurrentEstabelecimento(out var store, out var error)) return error!;
        try { return Ok(ApiResponse<T>.Ok(await action(store))); }
        catch (DeliveryDomainException ex) { return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details)); }
    }
}
