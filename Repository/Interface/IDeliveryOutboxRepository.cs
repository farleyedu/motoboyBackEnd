using APIBack.Model.Tracking;

namespace APIBack.Repository.Interface;

public interface IDeliveryOutboxRepository
{
    Task<IReadOnlyList<DeliveryOutboxRecord>?> ClaimAsync(Guid leaseId, int leaseSeconds, int limit, CancellationToken cancellationToken);
    Task<bool> RenewAsync(Guid leaseId, int leaseSeconds, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(Guid leaseId, Guid eventId, string? error, CancellationToken cancellationToken);
    Task ReleaseAsync(Guid leaseId, CancellationToken cancellationToken);
}
