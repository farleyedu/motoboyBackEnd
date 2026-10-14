namespace APIBack.DTOs.Delivery;

public sealed record IfoodActionCapabilityDto(string Command, bool Available, string? BlockedCode,
    DateTimeOffset? NotBeforeUtc = null, DateTimeOffset? DeadlineAtUtc = null);

public sealed class IfoodCapabilitiesDto
{
    public int PedidoId { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset ServerTimeUtc { get; set; }
    public IReadOnlyList<IfoodActionCapabilityDto> Actions { get; set; } = Array.Empty<IfoodActionCapabilityDto>();
    public IReadOnlyList<IfoodCommandDto> Operations { get; set; } = Array.Empty<IfoodCommandDto>();
}
