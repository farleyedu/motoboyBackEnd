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
    // Conclusão da entrega com o código oficial do cliente (exige Comandos e ao menos um merchant autorizado).
    public bool CompletionEnabled { get; set; }
    // Consumo no local (DINE_IN): libera aceitar, preparar, pronto e cancelar nessa modalidade.
    public bool DineInEnabled { get; set; }
    // Limpeza automática do corpo de eventos antigos (7 a 365 dias). Começa desligada.
    public bool RetentionEnabled { get; set; }
    public int RetentionDays { get; set; } = 30;
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
    public bool CompletionEnabled { get; set; }
    public bool DineInEnabled { get; set; }
    public bool RetentionEnabled { get; set; }
    public int RetentionDays { get; set; } = 30;
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
    // Pedidos NOVOS desta loja já nascem elegíveis à entrega Zippy (fila, rota e oferta ao motoboy).
    public bool ZippyDeliveryEnabled { get; set; }
}

public sealed class IfoodConnectionDeliveryRequest
{
    public bool Enabled { get; set; }
    // Também muda os pedidos de entrega da loja ainda abertos (os novos sempre herdam o valor da conexão).
    public bool ApplyToOpenOrders { get; set; }
}

public sealed class IfoodConnectionDeliveryResult
{
    public int OrdersUpdated { get; set; }
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
    public bool CompletionEnabled { get; set; }
    public bool DineInEnabled { get; set; }
}
