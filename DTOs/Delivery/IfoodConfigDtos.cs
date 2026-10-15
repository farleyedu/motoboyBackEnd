namespace APIBack.DTOs.Delivery;

public sealed class IfoodRuntimeSettingsDto
{
    public string ClientId { get; set; } = "";
    // Decifrado só nesta tela, por escolha explícita do operador (ver IF-13: write-only foi recusado).
    public string? ClientSecret { get; set; }
    public bool Enabled { get; set; }
    public bool PollingEnabled { get; set; }
    public bool ImportEnabled { get; set; }
    public bool ProjectionEnabled { get; set; }
    public bool CommandsEnabled { get; set; }
    public bool ExceptionsEnabled { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class IfoodRuntimeSettingsRequest
{
    public string ClientId { get; set; } = "";
    // Nulo/vazio preserva o segredo já gravado; só sobrescreve quando o operador digita um novo.
    public string? ClientSecret { get; set; }
    public bool Enabled { get; set; }
    public bool PollingEnabled { get; set; }
    public bool ImportEnabled { get; set; }
    public bool ProjectionEnabled { get; set; }
    public bool CommandsEnabled { get; set; }
    public bool ExceptionsEnabled { get; set; }
}

public sealed class IfoodConnectionDto
{
    public Guid MerchantId { get; set; }
    public Guid EstabelecimentoId { get; set; }
    public string Status { get; set; } = "";
    public DateTimeOffset? AuthorizedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public DateTimeOffset? LastEventAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class IfoodConnectionRequest
{
    public Guid MerchantId { get; set; }
    public Guid EstabelecimentoId { get; set; }
}

// Visão do restaurante: somente a conexão da própria loja e o estado geral da integração.
// Sem credenciais, sem lista de outras lojas.
public sealed class IfoodStoreStatusDto
{
    public IfoodConnectionDto? Connection { get; set; }
    public bool IntegrationEnabled { get; set; }
    public bool PollingEnabled { get; set; }
    public bool ImportEnabled { get; set; }
    public bool ProjectionEnabled { get; set; }
    public bool CommandsEnabled { get; set; }
}
