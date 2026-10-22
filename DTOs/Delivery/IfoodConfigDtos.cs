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

public sealed class IfoodStoreConnectionRequest
{
    // Texto, porque quem digita pode errar o formato: o servidor valida e explica.
    // ID da loja no iFood (UUID) OU CNPJ da loja: o servidor reconhece qual foi digitado. MerchantId segue aceito (clientes antigos).
    public string? Identifier { get; set; }
    public string? MerchantId { get; set; }
}

public sealed class IfoodMerchantCheckDto
{
    public bool Ok { get; set; }
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
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
    public IfoodConnectionHealthDto? Health { get; set; }
    public bool IntegrationEnabled { get; set; }
    public bool PollingEnabled { get; set; }
    public bool ImportEnabled { get; set; }
    public bool ProjectionEnabled { get; set; }
    public bool CommandsEnabled { get; set; }
    public bool CompletionEnabled { get; set; }
    public bool DineInEnabled { get; set; }
}

// Saude da conexao: o que o iFood confirmou, nao so o que a Zippy gravou.
public sealed class IfoodConnectionHealthDto
{
    public string State { get; set; } = "";
    public string Message { get; set; } = "";
    public string[] Actions { get; set; } = Array.Empty<string>();
    public string? PollState { get; set; }
    public string? PollErrorCode { get; set; }
    public int PollFailures { get; set; }
    public DateTimeOffset? PollBlockedUntilUtc { get; set; }
    public DateTimeOffset? LastPollAtUtc { get; set; }
    public DateTimeOffset? LastPollOkAtUtc { get; set; }
    public string? RemoteCheckCode { get; set; }
    public DateTimeOffset? RemoteCheckedAtUtc { get; set; }
}

public sealed class IfoodConnectionActivityDto
{
    public int EventsQueued { get; set; }
    public int EventsFailed { get; set; }
    public int EventsReview { get; set; }
    public int EventsUnacknowledged { get; set; }
    public int OpenOrders { get; set; }
    public DateTimeOffset? LastEventReceivedAtUtc { get; set; }
}

public sealed class IfoodConnectionDiagnosticDto
{
    public IfoodConnectionDto Connection { get; set; } = new();
    public IfoodConnectionHealthDto Health { get; set; } = new();
    public IfoodConnectionActivityDto? Activity { get; set; }
}

public sealed class IfoodDiagnosticsDto
{
    public bool IntegrationEnabled { get; set; }
    public bool PollingEnabled { get; set; }
    public DateTimeOffset? NextPollAtUtc { get; set; }
    public string? PollingErrorCode { get; set; }
    public List<IfoodConnectionDiagnosticDto> Connections { get; set; } = new();
}

public sealed class IfoodConnectionTransferRequest
{
    public Guid EstabelecimentoId { get; set; }
}

public sealed class IfoodAuditDto
{
    public long Id { get; set; }
    public Guid MerchantId { get; set; }
    public Guid? EstabelecimentoId { get; set; }
    public string Action { get; set; } = "";
    public string? Actor { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class IfoodStoreConnectResultDto
{
    public Guid MerchantId { get; set; }
    // Verdadeiro quando o restaurante digitou o CNPJ e a Zippy achou a loja no iFood.
    public bool ResolvedFromCnpj { get; set; }
}
