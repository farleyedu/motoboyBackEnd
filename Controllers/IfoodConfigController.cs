using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Delivery;
using APIBack.Extensions;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Config;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

// IF-13: configuração central do iFood (client/secret, autorização de merchant↔loja e os
// toggles globais) editável por tela em Gestão, sem variável de ambiente nem redeploy.
[ApiController, Route("api/v2/ifood/config")]
public sealed class IfoodConfigController(IIfoodRuntimeSettingsRepository repository, IfoodSecretProtector protector) : ControllerBase
{
    [HttpGet, RequirePermission("Delivery", "integracao_ifood")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(ApiResponse<IfoodRuntimeSettingsDto>.Ok(ToDto(await repository.GetSettingsAsync(ct))));
    }

    [HttpPut, RequirePermission("Delivery", "integracao_ifood")]
    public async Task<IActionResult> Put(IfoodRuntimeSettingsRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (string.IsNullOrWhiteSpace(request.ClientId))
            return BadRequest(ApiResponse<object>.Fail("Informe o Client ID.", "IFOOD_CONFIG_INVALID_CLIENT_ID"));
        string? cifrado = null;
        try
        {
            if (!string.IsNullOrEmpty(request.ClientSecret))
                cifrado = protector.Protect(request.ClientSecret, IfoodSecretProtector.ClientSecretPurpose);
        }
        catch (IfoodIntegrationException ex)
        { return StatusCode(503, ApiResponse<object>.Fail("Não foi possível cifrar o client secret.", ex.Code)); }
        var updatedBy = HttpContext.GetUserId()?.ToString();
        await repository.UpdateSettingsAsync(request.ClientId.Trim(), cifrado, request.Enabled, request.PollingEnabled,
            request.ImportEnabled, request.ProjectionEnabled, request.CommandsEnabled, request.ExceptionsEnabled, updatedBy, ct);
        return Ok(ApiResponse<IfoodRuntimeSettingsDto>.Ok(ToDto(await repository.GetSettingsAsync(ct))));
    }

    [HttpGet("connections"), RequirePermission("Delivery", "integracao_ifood")]
    public async Task<IActionResult> ListConnections(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var rows = await repository.ListConnectionsAsync(ct);
        return Ok(ApiResponse<IReadOnlyList<IfoodConnectionDto>>.Ok(rows.Select(ToDto).ToList()));
    }

    [HttpPost("connections"), RequirePermission("Delivery", "integracao_ifood")]
    public async Task<IActionResult> Authorize(IfoodConnectionRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            await repository.AuthorizeConnectionAsync(request.MerchantId, request.EstabelecimentoId, ct);
            return Ok(ApiResponse<object>.Ok(new { }));
        }
        catch (IfoodIntegrationException ex)
        {
            var status = ex.Code == "IFOOD_CONNECTION_MERCHANT_LINKED_ELSEWHERE" ? 409 : 400;
            return StatusCode(status, ApiResponse<object>.Fail("Não foi possível autorizar o merchant.", ex.Code));
        }
    }

    [HttpPut("connections/{merchantId:guid}/revoke"), RequirePermission("Delivery", "integracao_ifood")]
    public async Task<IActionResult> Revoke(Guid merchantId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try { await repository.RevokeConnectionAsync(merchantId, ct); return Ok(ApiResponse<object>.Ok(new { })); }
        catch (IfoodIntegrationException ex)
        { return StatusCode(404, ApiResponse<object>.Fail("Conexão não encontrada ou já revogada.", ex.Code)); }
    }

    private IfoodRuntimeSettingsDto ToDto(IfoodRuntimeSettings settings) => new()
    {
        ClientId = settings.ClientId,
        ClientSecret = string.IsNullOrEmpty(settings.ClientSecretCifrado) ? null : SafeUnprotect(settings.ClientSecretCifrado),
        Enabled = settings.Enabled, PollingEnabled = settings.PollingEnabled, ImportEnabled = settings.ImportEnabled,
        ProjectionEnabled = settings.ProjectionEnabled, CommandsEnabled = settings.CommandsEnabled, ExceptionsEnabled = settings.ExceptionsEnabled,
        UpdatedAtUtc = settings.UpdatedAtUtc, UpdatedBy = settings.UpdatedBy
    };
    // Segredo gravado com chave antiga/rotacionada de forma incompatível não deve derrubar a tela.
    private string? SafeUnprotect(string cifrado)
    { try { return protector.Unprotect(cifrado, IfoodSecretProtector.ClientSecretPurpose); } catch (IfoodIntegrationException) { return null; } }
    private static IfoodConnectionDto ToDto(IfoodConnectionRow row) => new()
    {
        MerchantId = row.MerchantId, EstabelecimentoId = row.EstabelecimentoId, Status = row.Status,
        AuthorizedAtUtc = row.AuthorizedAtUtc, RevokedAtUtc = row.RevokedAtUtc, LastEventAtUtc = row.LastEventAtUtc, CreatedAtUtc = row.CreatedAtUtc
    };
}
