namespace APIBack.Integrations.Ifood.Config;

public sealed class IfoodRuntimeSettings
{
    public string ClientId { get; set; } = "";
    // Nunca em texto puro fora deste processo; cifrado por IfoodSecretProtector antes de persistir.
    public string? ClientSecretCifrado { get; set; }
    public bool Enabled { get; set; }
    public bool PollingEnabled { get; set; }
    public bool ImportEnabled { get; set; }
    public bool ProjectionEnabled { get; set; }
    public bool CommandsEnabled { get; set; }
    public bool ExceptionsEnabled { get; set; }
    public bool CompletionEnabled { get; set; }
    public bool DineInEnabled { get; set; }
    public bool RetentionEnabled { get; set; }
    public int RetentionDays { get; set; } = 30;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class IfoodConnectionRow
{
    public Guid MerchantId { get; set; }
    public Guid EstabelecimentoId { get; set; }
    public string Status { get; set; } = "";
    public DateTimeOffset? AuthorizedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public DateTimeOffset? LastEventAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public bool ZippyDeliveryEnabled { get; set; }
}
