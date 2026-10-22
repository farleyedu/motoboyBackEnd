using APIBack.Attributes;
using APIBack.DTOs.Cardapio;
using APIBack.DTOs.Common;
using APIBack.Repository;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

/// <summary>
/// Liga os itens dos pedidos iFood aos produtos do cardápio da loja (só exibição: foto, categoria, aviso de bag). Nunca altera
/// preço, quantidade ou total do pedido. A loja vem sempre do token; produto ou vínculo de outra loja responde 404.
/// </summary>
[ApiController]
public sealed class CardapioIfoodVinculoController(
    IfoodProdutoVinculoRepository repository,
    ICardapioContractService cardapio,
    CardapioImagemService imagens,
    ILogger<CardapioIfoodVinculoController> logger) : EstabelecimentoScopedControllerBase
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

    /// <summary>
    /// Cria o produto a partir do item pendente e já liga os dois. O produto nasce oculto do cardápio web (o preço do iFood
    /// costuma ser maior que o do canal próprio); a foto do iFood é copiada para o nosso armazenamento quando possível.
    /// Se o vínculo falhar depois de criado, o produto é excluído: não sobra produto solto.
    /// </summary>
    [HttpPost("api/v2/cardapio/ifood/pendentes/{vinculoId:guid}/criar-produto")]
    [RequirePermission("Cardapio", "criar")]
    public async Task<IActionResult> CriarProduto(Guid vinculoId, [FromBody] SalvarCardapioProdutoContractRequest? request, CancellationToken ct)
    {
        if (request == null) return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
        if (!TryResolveCurrentEstabelecimentoAndModule("Cardapio", out var store, out var error)) return error!;
        try
        {
            var imagemIfood = await repository.PrepararCriacaoAsync(store, vinculoId, request.Nome, ct);
            request.PublicadoWeb = false;
            request.ImageUrl = await HospedarFotoAsync(store, request.ImageUrl, imagemIfood, ct);

            var produto = await cardapio.CriarProdutoAsync(store, request);
            IfoodProdutoVinculoDto vinculo;
            try
            {
                vinculo = await repository.VincularAsync(store, CurrentUserId, produto.Id, vinculoId, CancellationToken.None);
            }
            catch
            {
                await cardapio.ExcluirProdutoAsync(store, produto.Id);
                throw;
            }
            return Ok(ApiResponse<IfoodProdutoCriadoDto>.Ok(new IfoodProdutoCriadoDto { Produto = produto, Vinculo = vinculo }));
        }
        catch (DeliveryDomainException ex) { return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details)); }
        catch (RequestValidationException ex) { return ValidationErrorResponse(ex); }
        catch (InvalidOperationException ex) { return ConflictErrorResponse(ex.Message); }
    }

    /// <summary>Foto do iFood (ou vazia com foto no iFood) vira cópia nossa; falha na cópia cria o produto sem foto.</summary>
    private async Task<string?> HospedarFotoAsync(Guid store, string? enviada, string? imagemIfood, CancellationToken ct)
    {
        var url = string.IsNullOrWhiteSpace(enviada) ? null : enviada.Trim();
        if (url == null || !string.Equals(url, imagemIfood?.Trim(), StringComparison.Ordinal)) return url;
        try
        {
            return (await imagens.ImportarDeUrlAsync(store, url, ct)).Url;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Nao foi possivel copiar a foto do iFood {VinculoUrl}; produto criado sem foto.", url);
            return null;
        }
    }

    private async Task<IActionResult> RunAsync<T>(Func<Guid, Task<T>> action)
    {
        if (!TryResolveCurrentEstabelecimento(out var store, out var error)) return error!;
        try { return Ok(ApiResponse<T>.Ok(await action(store))); }
        catch (DeliveryDomainException ex) { return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details)); }
    }
}
