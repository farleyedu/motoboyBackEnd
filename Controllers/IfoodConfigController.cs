using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Delivery;
using APIBack.Extensions;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Config;
using APIBack.Integrations.Ifood.Connections;
using APIBack.Integrations.Ifood.Merchants;
using APIBack.Integrations.Ifood.Reconciliation;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

// IF-13: configuração central do iFood (client/secret, autorização de merchant↔loja e os
// toggles globais) editável por tela em Gestão, sem variável de ambiente nem redeploy.
[ApiController, Route("api/v2/ifood/config")]
public sealed class IfoodConfigController(IIfoodRuntimeSettingsRepository repository, IfoodSecretProtector protector,
    IIfoodMerchantProbe? probe = null, IIfoodConnectionHealth? health = null, IIfoodConnectionLifecycle? lifecycle = null,
    IIfoodReconciliationRepository? reconciliation = null, TimeProvider? clock = null) : ControllerBase
{
    private readonly IIfoodConnectionHealth _health = health ?? NullIfoodConnectionHealth.Instance;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    [HttpGet, RequirePermission("Delivery", "integracao_ifood")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!HttpContext.IsSuperAdmin()) return SuperAdminRequired();
        Response.Headers.CacheControl = "no-store";
        return Ok(ApiResponse<IfoodRuntimeSettingsDto>.Ok(ToDto(await repository.GetSettingsAsync(ct))));
    }

    [HttpPut, RequirePermission("Delivery", "integracao_ifood")]
    public async Task<IActionResult> Put(IfoodRuntimeSettingsRequest request, CancellationToken ct)
    {
        if (!HttpContext.IsSuperAdmin()) return SuperAdminRequired();
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
            request.ImportEnabled, request.ProjectionEnabled, request.CommandsEnabled, request.ExceptionsEnabled, updatedBy, request.CompletionEnabled, request.RetentionEnabled, request.RetentionDays, request.DineInEnabled, ct);
        return Ok(ApiResponse<IfoodRuntimeSettingsDto>.Ok(ToDto(await repository.GetSettingsAsync(ct))));
    }

    [HttpGet("connections"), RequirePermission("Delivery", "integracao_ifood")]
    public async Task<IActionResult> ListConnections(CancellationToken ct)
    {
        if (!HttpContext.IsSuperAdmin()) return SuperAdminRequired();
        Response.Headers.CacheControl = "no-store";
        var rows = await repository.ListConnectionsAsync(ct);
        return Ok(ApiResponse<IReadOnlyList<IfoodConnectionDto>>.Ok(rows.Select(IfoodConnectionMapper.ToDto).ToList()));
    }

    /// <summary>Painel de diagnóstico: por conexão, o que o iFood confirmou, o polling, os eventos e o que fazer.</summary>
    [HttpGet("diagnostics"), RequirePermission("Delivery", "integracao_ifood")]
    public async Task<IActionResult> Diagnostics(CancellationToken ct)
    {
        if (!HttpContext.IsSuperAdmin()) return SuperAdminRequired();
        Response.Headers.CacheControl = "no-store";
        var settings = await repository.GetSettingsAsync(ct);
        var now = _clock.GetUtcNow();
        var rows = await repository.ListConnectionsAsync(ct);
        var activity = lifecycle is null ? new List<IfoodConnectionActivity>() : (await lifecycle.ActivityAsync(null, ct)).ToList();
        var polling = reconciliation is null ? null : await reconciliation.GetPollingHealthAsync(ct);
        var dto = new IfoodDiagnosticsDto
        {
            IntegrationEnabled = settings.Enabled, PollingEnabled = settings.Enabled && settings.PollingEnabled,
            NextPollAtUtc = polling?.NextPollAtUtc, PollingErrorCode = polling?.LastErrorCode,
            Connections = rows.Select(row => new IfoodConnectionDiagnosticDto
            {
                Connection = IfoodConnectionMapper.ToDto(row),
                Health = IfoodConnectionMapper.ToHealthDto(row, now),
                Activity = activity.Where(a => a.MerchantId == row.MerchantId && a.EstabelecimentoId == row.EstabelecimentoId)
                    .Select(a => new IfoodConnectionActivityDto { EventsQueued = a.EventsQueued, EventsFailed = a.EventsFailed, EventsReview = a.EventsReview,
                        EventsUnacknowledged = a.EventsUnacknowledged, OpenOrders = a.OpenOrders, LastEventReceivedAtUtc = a.LastEventReceivedAtUtc }).FirstOrDefault()
            }).ToList()
        };
        return Ok(ApiResponse<IfoodDiagnosticsDto>.Ok(dto));
    }

    [HttpGet("connections/audit"), RequirePermission("Delivery", "integracao_ifood")]
    public async Task<IActionResult> Audit([FromQuery] Guid? merchantId, [FromQuery] Guid? estabelecimentoId, CancellationToken ct)
    {
        if (!HttpContext.IsSuperAdmin()) return SuperAdminRequired();
        Response.Headers.CacheControl = "no-store";
        if (lifecycle is null) return Ok(ApiResponse<IReadOnlyList<IfoodAuditDto>>.Ok(Array.Empty<IfoodAuditDto>()));
        var rows = await lifecycle.ListAuditAsync(merchantId, estabelecimentoId, 100, ct);
        return Ok(ApiResponse<IReadOnlyList<IfoodAuditDto>>.Ok(rows.Select(r => new IfoodAuditDto
        { Id = r.Id, MerchantId = r.MerchantId, EstabelecimentoId = r.EstabelecimentoId, Action = r.Action, Actor = r.Actor, Detail = r.Detail, CreatedAtUtc = r.CreatedAtUtc }).ToList()));
    }

    /// <summary>
    /// Autorizar exige que o iFood confirme agora: o status gravado na Zippy nunca substitui a autorização real.
    /// </summary>
    [HttpPost("connections"), RequirePermission("Delivery", "integracao_ifood")]
    public async Task<IActionResult> Authorize(IfoodConnectionRequest request, CancellationToken ct)
    {
        if (!HttpContext.IsSuperAdmin()) return SuperAdminRequired();
        Response.Headers.CacheControl = "no-store";
        if (probe is not null && request.MerchantId != Guid.Empty)
        {
            var check = await probe.CheckAsync(request.MerchantId, ct);
            if (!check.Ok)
            {
                var transient = check.Code is "RATE_LIMIT" or "UNAVAILABLE" or "APP_CREDENTIALS" or "DISABLED";
                return StatusCode(transient ? 503 : 422, ApiResponse<object>.Fail(check.Message,
                    check.Code == "NOT_AUTHORIZED" ? "IFOOD_MERCHANT_NOT_AUTHORIZED_AT_IFOOD" : "IFOOD_MERCHANT_CHECK_" + check.Code));
            }
        }
        try
        {
            await repository.AuthorizeConnectionAsync(request.MerchantId, request.EstabelecimentoId, ct);
            if (probe is not null) await _health.RecordRemoteCheckAsync(request.MerchantId, "AUTHORIZED", ct);
            await AuditAsync(request.MerchantId, request.EstabelecimentoId, "authorized", ct);
            return Ok(ApiResponse<object>.Ok(new { }));
        }
        catch (IfoodIntegrationException ex)
        { return LifecycleError(ex, "Não foi possível autorizar o merchant."); }
    }

    /// <summary>Pergunta ao iFood agora e, se estiver tudo certo, tira o merchant da quarentena para o próximo ciclo.</summary>
    [HttpPost("connections/{merchantId:guid}/recheck"), RequirePermission("Delivery", "integracao_ifood")]
    public async Task<IActionResult> Recheck(Guid merchantId, CancellationToken ct)
    {
        if (!HttpContext.IsSuperAdmin()) return SuperAdminRequired();
        Response.Headers.CacheControl = "no-store";
        if (probe is null) return StatusCode(503, ApiResponse<object>.Fail("Verificação indisponível.", "IFOOD_CHECK_UNAVAILABLE"));
        var check = await probe.CheckAsync(merchantId, ct);
        await _health.RecordRemoteCheckAsync(merchantId, check.Code, ct);
        if (check.Ok) await _health.RetryNowAsync(merchantId, ct);
        await AuditAsync(merchantId, null, "rechecked", ct, check.Code);
        return Ok(ApiResponse<IfoodMerchantCheckDto>.Ok(new IfoodMerchantCheckDto { Ok = check.Ok, Code = check.Code, Message = check.Message }));
    }

    [HttpPut("connections/{merchantId:guid}/revoke"), RequirePermission("Delivery", "integracao_ifood")]
    public async Task<IActionResult> Revoke(Guid merchantId, CancellationToken ct)
    {
        if (!HttpContext.IsSuperAdmin()) return SuperAdminRequired();
        Response.Headers.CacheControl = "no-store";
        try
        {
            await repository.RevokeConnectionAsync(merchantId, ct);
            await AuditAsync(merchantId, null, "revoked", ct);
            return Ok(ApiResponse<object>.Ok(new { }));
        }
        catch (IfoodIntegrationException ex)
        { return StatusCode(404, ApiResponse<object>.Fail("Conexão não encontrada ou já revogada.", ex.Code)); }
    }

    /// <summary>Muda o merchant de estabelecimento sem SQL: a loja antiga mantém o histórico, a nova fica pendente até autorizar.</summary>
    [HttpPost("connections/{merchantId:guid}/transfer"), RequirePermission("Delivery", "integracao_ifood")]
    public async Task<IActionResult> Transfer(Guid merchantId, IfoodConnectionTransferRequest request, CancellationToken ct)
    {
        if (!HttpContext.IsSuperAdmin()) return SuperAdminRequired();
        Response.Headers.CacheControl = "no-store";
        if (lifecycle is null) return StatusCode(503, ApiResponse<object>.Fail("Transferência indisponível.", "IFOOD_LIFECYCLE_UNAVAILABLE"));
        try
        {
            await lifecycle.TransferAsync(merchantId, request.EstabelecimentoId, HttpContext.GetUserId()?.ToString(), ct);
            return Ok(ApiResponse<object>.Ok(new { }));
        }
        catch (IfoodIntegrationException ex)
        { return LifecycleError(ex, "Não foi possível transferir a conexão."); }
    }

    /// <summary>Remove só o que nunca foi usado (pendente ou desfeita, sem eventos nem pedidos). Com histórico, o banco recusa.</summary>
    [HttpDelete("connections/{merchantId:guid}/{estabelecimentoId:guid}"), RequirePermission("Delivery", "integracao_ifood")]
    public async Task<IActionResult> Remove(Guid merchantId, Guid estabelecimentoId, CancellationToken ct)
    {
        if (!HttpContext.IsSuperAdmin()) return SuperAdminRequired();
        Response.Headers.CacheControl = "no-store";
        if (lifecycle is null) return StatusCode(503, ApiResponse<object>.Fail("Remoção indisponível.", "IFOOD_LIFECYCLE_UNAVAILABLE"));
        try
        {
            await lifecycle.RemoveUnusedAsync(merchantId, estabelecimentoId, HttpContext.GetUserId()?.ToString(), ct);
            return Ok(ApiResponse<object>.Ok(new { }));
        }
        catch (IfoodIntegrationException ex)
        { return LifecycleError(ex, "Não foi possível remover a conexão."); }
    }

    [HttpPut("connections/{merchantId:guid}/delivery"), RequirePermission("Delivery", "integracao_ifood")]
    public async Task<IActionResult> SetDelivery(Guid merchantId, IfoodConnectionDeliveryRequest request, CancellationToken ct)
    {
        // Quem gerencia a loja (permissão de operar o iFood) liga e desliga a Entrega Zippy, mas só na conexão da própria
        // loja; a Gestão (super admin) altera qualquer uma.
        var superAdmin = HttpContext.IsSuperAdmin();
        var store = superAdmin ? null : HttpContext.GetEstabelecimentoId();
        if (!superAdmin && store is null)
            return BadRequest(ApiResponse<object>.Fail("Selecione um estabelecimento.", "IFOOD_STORE_NOT_SELECTED"));
        Response.Headers.CacheControl = "no-store";
        try
        {
            var updated = await repository.SetConnectionDeliveryAsync(merchantId, request.Enabled, request.ApplyToOpenOrders, ct, store);
            await AuditAsync(merchantId, store, request.Enabled ? "zippy_delivery_enabled" : "zippy_delivery_disabled", ct);
            return Ok(ApiResponse<IfoodConnectionDeliveryResult>.Ok(new IfoodConnectionDeliveryResult { OrdersUpdated = updated }));
        }
        catch (IfoodIntegrationException ex)
        { return StatusCode(404, ApiResponse<object>.Fail("Conexão não encontrada ou revogada.", ex.Code)); }
    }

    private async Task AuditAsync(Guid merchantId, Guid? store, string action, CancellationToken ct, string? detail = null)
    {
        if (lifecycle is null) return;
        try { await lifecycle.AuditAsync(merchantId, store, action, HttpContext.GetUserId()?.ToString(), detail, ct); }
        catch (IfoodIntegrationException) { /* a auditoria nunca desfaz a operação já concluída */ }
    }

    internal static IActionResult LifecycleError(IfoodIntegrationException ex, string fallback)
    {
        var (status, message) = ex.Code switch
        {
            "IFOOD_CONNECTION_MERCHANT_LINKED_ELSEWHERE" => (409, "Este merchant está ativo em outra loja. Use “Transferir” para mudá-lo de estabelecimento."),
            "IFOOD_CONNECTION_STORE_HAS_ACTIVE" => (409, "Esta loja já tem uma conexão ativa com o iFood. Desconecte antes de ligar outro merchant."),
            "IFOOD_CONNECTION_HAS_PENDING_WORK" => (409, "Ainda há pedidos abertos ou eventos sem tratamento nesta conexão. Conclua-os antes de transferir."),
            "IFOOD_CONNECTION_HAS_HISTORY" => (409, "Esta conexão tem pedidos ou eventos no histórico e não pode ser apagada. Ela fica desconectada e o histórico é mantido."),
            "IFOOD_CONNECTION_IN_USE" => (409, "A conexão está autorizada. Desconecte antes de remover."),
            "IFOOD_CONNECTION_NOT_FOUND" => (404, "Conexão não encontrada."),
            _ => (400, fallback)
        };
        return new ObjectResult(ApiResponse<object>.Fail(message, ex.Code)) { StatusCode = status };
    }

    // Credenciais e interruptores são globais: só administradores Zippy. O segredo nunca
    // é devolvido; valor vazio no PUT preserva o já gravado. Restaurantes usam api/v2/ifood/store.
    private IActionResult SuperAdminRequired() => StatusCode(403,
        ApiResponse<object>.Fail("Apenas administradores Zippy podem alterar a configuração global do iFood.", "IFOOD_CONFIG_SUPERADMIN_REQUIRED"));

    private IfoodRuntimeSettingsDto ToDto(IfoodRuntimeSettings settings) => new()
    {
        ClientId = settings.ClientId,
        ClientSecret = null,
        Enabled = settings.Enabled, PollingEnabled = settings.PollingEnabled, ImportEnabled = settings.ImportEnabled,
        ProjectionEnabled = settings.ProjectionEnabled, CommandsEnabled = settings.CommandsEnabled, ExceptionsEnabled = settings.ExceptionsEnabled,
        CompletionEnabled = settings.CompletionEnabled, DineInEnabled = settings.DineInEnabled, RetentionEnabled = settings.RetentionEnabled, RetentionDays = settings.RetentionDays, UpdatedAtUtc = settings.UpdatedAtUtc, UpdatedBy = settings.UpdatedBy
    };
}

internal static class IfoodConnectionMapper
{
    public static IfoodConnectionDto ToDto(IfoodConnectionRow row) => new()
    {
        MerchantId = row.MerchantId, EstabelecimentoId = row.EstabelecimentoId, Status = row.Status,
        AuthorizedAtUtc = row.AuthorizedAtUtc, RevokedAtUtc = row.RevokedAtUtc, LastEventAtUtc = row.LastEventAtUtc, CreatedAtUtc = row.CreatedAtUtc,
        ZippyDeliveryEnabled = row.ZippyDeliveryEnabled
    };

    public static IfoodConnectionHealthDto ToHealthDto(IfoodConnectionRow row, DateTimeOffset now)
    {
        var view = IfoodConnectionState.Describe(row, now);
        return new IfoodConnectionHealthDto
        {
            State = view.State, Message = view.Message, Actions = view.Actions,
            PollState = row.PollState, PollErrorCode = row.PollErrorCode, PollFailures = row.PollFailures, PollBlockedUntilUtc = row.PollBlockedUntilUtc,
            LastPollAtUtc = row.LastPollAtUtc, LastPollOkAtUtc = row.LastPollOkAtUtc, RemoteCheckCode = row.RemoteCheckCode, RemoteCheckedAtUtc = row.RemoteCheckedAtUtc
        };
    }
}

// Visão do restaurante: o estado da própria loja e o que ela faz sozinha (informar o merchant e desconectar).
// Quem autoriza de verdade a conexão é a Gestão; sem credenciais e sem lista de outras lojas.
[ApiController, Route("api/v2/ifood/store")]
public sealed class IfoodStoreController(IIfoodRuntimeSettingsRepository repository, IIfoodMerchantProbe probe,
    IIfoodMerchantDirectory directory, IIfoodConnectionHealth health, IIfoodConnectionLifecycle lifecycle, TimeProvider clock) : ControllerBase
{
    /// <summary>
    /// A loja informa o ID dela no iFood ou o CNPJ. Fica "aguardando": a Gestão confere a autorização no iFood e liga.
    /// Com CNPJ, a Zippy procura a loja entre as que já autorizaram o app no iFood; sem achar, nada é vinculado.
    /// </summary>
    [HttpPost("connection"), RequirePermission("Delivery", "integracao_ifood")]
    public async Task<IActionResult> Connect(IfoodStoreConnectionRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var store = HttpContext.GetEstabelecimentoId();
        if (store is null)
            return BadRequest(ApiResponse<object>.Fail("Selecione um estabelecimento.", "IFOOD_STORE_NOT_SELECTED"));
        var identifier = IfoodStoreIdentifier.Parse(request.Identifier ?? request.MerchantId);
        if (identifier.Kind == IfoodIdentifierKind.Invalid)
            return BadRequest(ApiResponse<object>.Fail("Informe o ID da loja no iFood (formato 00000000-0000-0000-0000-000000000000) ou o CNPJ da loja.", "IFOOD_CONNECTION_INVALID_INPUT"));

        var merchantId = identifier.MerchantId;
        if (identifier.Kind == IfoodIdentifierKind.Cnpj)
        {
            // Só o CNPJ do próprio estabelecimento: impede ligar a loja de outra empresa por engano ou de propósito.
            var registered = await lifecycle.GetStoreCnpjAsync(store.Value, ct);
            if (registered is not null && registered != identifier.CnpjDigits)
                return Conflict(ApiResponse<object>.Fail("Este CNPJ é diferente do cadastrado para este estabelecimento.", "IFOOD_CNPJ_MISMATCH"));
            var lookup = await directory.FindByCnpjAsync(identifier.CnpjDigits, ct);
            if (!lookup.Found)
                return lookup.Code == "NOT_FOUND"
                    ? UnprocessableEntity(ApiResponse<object>.Fail("O iFood não mostrou uma loja com este CNPJ autorizada para a Zippy. Autorize a Zippy no Portal do Parceiro e tente de novo, ou informe o ID da loja.", "IFOOD_CNPJ_NOT_FOUND"))
                    : StatusCode(503, ApiResponse<object>.Fail(lookup.Message, "IFOOD_CNPJ_LOOKUP_" + lookup.Code));
            merchantId = lookup.MerchantId;
        }
        try
        {
            await repository.RequestStoreConnectionAsync(merchantId, store.Value, ct);
        }
        catch (IfoodIntegrationException ex)
        {
            return ex.Code switch
            {
                "IFOOD_CONNECTION_MERCHANT_LINKED_ELSEWHERE" => StatusCode(409, ApiResponse<object>.Fail("Este ID já está ligado a outra loja. Se ele é seu, fale com a Zippy.", ex.Code)),
                "IFOOD_CONNECTION_STORE_HAS_ACTIVE" => StatusCode(409, ApiResponse<object>.Fail("Esta loja já tem uma conexão com o iFood. Desconecte antes de informar outro ID.", ex.Code)),
                _ => BadRequest(ApiResponse<object>.Fail("Não foi possível registrar o ID da loja.", ex.Code))
            };
        }
        await AuditSafeAsync(merchantId, store.Value, "connect_requested", identifier.Kind == IfoodIdentifierKind.Cnpj ? "via CNPJ" : "via ID", ct);
        return Ok(ApiResponse<IfoodStoreConnectResultDto>.Ok(new IfoodStoreConnectResultDto { MerchantId = merchantId, ResolvedFromCnpj = identifier.Kind == IfoodIdentifierKind.Cnpj }));
    }

    [HttpDelete("connection"), RequirePermission("Delivery", "integracao_ifood")]
    public async Task<IActionResult> Disconnect(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var store = HttpContext.GetEstabelecimentoId();
        if (store is null)
            return BadRequest(ApiResponse<object>.Fail("Selecione um estabelecimento.", "IFOOD_STORE_NOT_SELECTED"));
        var current = await repository.GetStoreConnectionAsync(store.Value, ct);
        var disconnected = await repository.DisconnectStoreAsync(store.Value, ct);
        if (disconnected && current is not null) await AuditSafeAsync(current.MerchantId, store.Value, "disconnected", null, ct);
        return disconnected
            ? Ok(ApiResponse<object>.Ok(new { }))
            : NotFound(ApiResponse<object>.Fail("Esta loja não tem conexão com o iFood.", "IFOOD_CONNECTION_NOT_FOUND"));
    }

    /// <summary>Pergunta ao iFood, agora, se esta loja está autorizada para a Zippy. Se estiver, o polling volta a tentá-la já no próximo ciclo.</summary>
    [HttpPost("connection/verify"), RequirePermission("Delivery", "integracao_ifood")]
    public async Task<IActionResult> Verify(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var store = HttpContext.GetEstabelecimentoId();
        if (store is null)
            return BadRequest(ApiResponse<object>.Fail("Selecione um estabelecimento.", "IFOOD_STORE_NOT_SELECTED"));
        var row = await repository.GetStoreConnectionAsync(store.Value, ct);
        if (row is null || row.RevokedAtUtc is not null)
            return Ok(ApiResponse<IfoodMerchantCheckDto>.Ok(new IfoodMerchantCheckDto { Ok = false, Code = "NOT_CONNECTED", Message = "Esta loja ainda não informou o ID do iFood." }));
        var check = await probe.CheckAsync(row.MerchantId, ct);
        await health.RecordRemoteCheckAsync(row.MerchantId, check.Code, ct);
        if (check.Ok) await health.RetryNowAsync(row.MerchantId, ct);
        return Ok(ApiResponse<IfoodMerchantCheckDto>.Ok(new IfoodMerchantCheckDto { Ok = check.Ok, Code = check.Code, Message = check.Message }));
    }

    [HttpGet, RequirePermission("Delivery", "integracao_ifood")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var store = HttpContext.GetEstabelecimentoId();
        if (store is null)
            return BadRequest(ApiResponse<object>.Fail("Selecione um estabelecimento.", "IFOOD_STORE_NOT_SELECTED"));
        return await StatusOfAsync(store.Value, ct);
    }

    /// <summary>
    /// Estado do iFood de uma loja escolhida pela tela (Gestão > Lojas > Detalhes), sem depender da loja ativa na sessão.
    /// A Gestão lê qualquer loja; os demais só a própria.
    /// </summary>
    [HttpGet("{estabelecimentoId:guid}"), RequirePermission("Delivery", "integracao_ifood")]
    public async Task<IActionResult> GetByStore(Guid estabelecimentoId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (!HttpContext.IsSuperAdmin() && HttpContext.GetEstabelecimentoId() != estabelecimentoId)
            return StatusCode(403, ApiResponse<object>.Fail("Acesso negado a este estabelecimento.", "IFOOD_STORE_FORBIDDEN"));
        return await StatusOfAsync(estabelecimentoId, ct);
    }

    private async Task<IActionResult> StatusOfAsync(Guid storeId, CancellationToken ct)
    {
        var settings = await repository.GetSettingsAsync(ct);
        var row = await repository.GetStoreConnectionAsync(storeId, ct);
        var enabled = settings.Enabled && !string.IsNullOrWhiteSpace(settings.ClientId) && !string.IsNullOrWhiteSpace(settings.ClientSecretCifrado);
        return Ok(ApiResponse<IfoodStoreStatusDto>.Ok(new IfoodStoreStatusDto
        {
            Connection = row is null ? null : IfoodConnectionMapper.ToDto(row),
            Health = row is null ? null : IfoodConnectionMapper.ToHealthDto(row, clock.GetUtcNow()),
            IntegrationEnabled = enabled, PollingEnabled = enabled && settings.PollingEnabled, ImportEnabled = enabled && settings.ImportEnabled,
            ProjectionEnabled = enabled && settings.ProjectionEnabled, CommandsEnabled = enabled && settings.CommandsEnabled,
            CompletionEnabled = enabled && settings.CommandsEnabled && settings.CompletionEnabled,
            DineInEnabled = enabled && settings.CommandsEnabled && settings.DineInEnabled
        }));
    }

    private async Task AuditSafeAsync(Guid merchantId, Guid store, string action, string? detail, CancellationToken ct)
    {
        try { await lifecycle.AuditAsync(merchantId, store, action, HttpContext.GetUserId()?.ToString(), detail, ct); }
        catch (IfoodIntegrationException) { /* a auditoria nunca desfaz a operação já concluída */ }
    }
}
