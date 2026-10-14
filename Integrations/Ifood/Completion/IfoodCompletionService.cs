using APIBack.DTOs.Delivery;
using APIBack.Repository;
using APIBack.Service;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.Integrations.Ifood.Completion;

public sealed class IfoodCompletionService(NpgsqlDataSource source, IOptionsMonitor<IfoodOptions> options,
    IfoodSecretProtector secrets, IIfoodDeliveryCodeClient client)
{
    private readonly PedidoQueueRepository _repository = new(source);

    public Task<IfoodCompletionOperationDto> EnqueueAsync(Guid store, int rider, int user, Guid session, long epoch,
        DeliveryCompletionRequest request, CancellationToken ct) =>
        _repository.EnqueueIfoodCompletionAsync(store, rider, user, session, epoch, request, options.CurrentValue, secrets, ct);

    public Task<IfoodCompletionOperationDto?> GetAsync(int user, Guid operation, CancellationToken ct) =>
        _repository.GetIfoodCompletionAsync(user, operation, ct);
    public Task<IfoodCompletionOperationDto> EnqueuePickupAsync(Guid store, int user, int pedido, IfoodPickupCompletionRequest request, CancellationToken ct) =>
        _repository.EnqueueIfoodPickupAsync(store, user, pedido, request, options.CurrentValue, secrets, ct);
    public Task<IfoodPickupCompletionContext> PickupContextAsync(Guid store, int pedido, CancellationToken ct) =>
        _repository.GetIfoodPickupContextAsync(store, pedido, options.CurrentValue, ct);
    public Task<IfoodCompletionOperationDto?> GetForStoreAsync(Guid store, Guid operation, CancellationToken ct) =>
        _repository.GetIfoodCompletionForStoreAsync(store, operation, ct);

    public async Task ProcessAsync(CancellationToken ct)
    {
        // Recuperação local continua mesmo com envio desligado: nenhuma chamada remota nesses caminhos.
        await _repository.RecoverIfoodCompletionsAsync(ct);
        var settings = options.CurrentValue;
        if (!settings.Enabled || !settings.CommandsEnabled || !settings.CompletionEnabled) return;
        foreach (var operation in await _repository.PendingIfoodCompletionsAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            IfoodCompletionReservation? reservation = null;
            try
            {
                reservation = await _repository.ClaimIfoodCompletionAsync(operation, options.CurrentValue, ct);
                if (reservation == null) continue;
                var code = secrets.Unprotect(reservation.CodeEnvelope, reservation.Purpose);
                var result = await client.SendAsync(reservation.Store, reservation.Merchant, reservation.Order, code,
                    token => _repository.MarkIfoodCompletionSendingAsync(reservation, options.CurrentValue, token), ct);
                await _repository.SaveIfoodCompletionResultAsync(reservation, result, ct);
                await _repository.RecoverIfoodCompletionsAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IfoodIntegrationException or DeliveryDomainException)
            {
                // Não usar mensagens de exceção/payload em diagnóstico; erros anteriores ao envio permitem recuperação.
                if (reservation != null) await _repository.ReleaseIfoodCompletionAsync(reservation, ct);
            }
        }
    }
}

internal sealed record IfoodCompletionReservation(Guid Operation, Guid Lease, Guid Store, Guid Merchant, Guid Order,
    int Rider, int Pedido, string CodeEnvelope)
{
    public string Purpose => "completion_" + Operation.ToString("N");
}

public sealed class IfoodCompletionWorker(IfoodCompletionService service, ILogger<IfoodCompletionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await service.ProcessAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("IFOOD_COMPLETION_WORKER_RECOVERY_REQUIRED"); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
