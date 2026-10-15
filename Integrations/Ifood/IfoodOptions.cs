using Microsoft.Extensions.Options;
using System.Text.Json.Serialization;

namespace APIBack.Integrations.Ifood;

public sealed class IfoodOptions
{
    public const string SectionName = "Ifood";
    public bool Enabled { get; set; }
    public string ClientId { get; set; } = "";
    [JsonIgnore] public string ClientSecret { get; set; } = "";
    // Preenchido somente pela configuração central (IF-13), nunca por appsettings/env; decifrado sob demanda via IfoodSecretProtector.ResolveClientSecret.
    [JsonIgnore] public string? ClientSecretCifrado { get; set; }
    public int HttpTimeoutSeconds { get; set; } = 15;
    public int RenewalSkewSeconds { get; set; } = 60;
    public int FailureCooldownSeconds { get; set; } = 10;
    public bool PollingEnabled { get; set; }
    public bool ImportEnabled { get; set; }
    public bool ProjectionEnabled { get; set; }
    public int ProjectionLeaseSeconds { get; set; } = 90;
    public int ProjectionBatchSize { get; set; } = 20;
    public int ProjectionMaxAttempts { get; set; } = 8;
    public bool CommandsEnabled { get; set; }
    // IF-14: habilitação explícita; reutiliza a allowlist merchant/loja dos comandos.
    public bool CompletionEnabled { get; set; }
    // Limpeza automática do corpo de eventos antigos (ligada e dimensionada pela tela).
    public bool RetentionEnabled { get; set; }
    public int RetentionDays { get; set; } = 30;
    public int CommandLeaseSeconds { get; set; } = 90;
    public int CommandBatchSize { get; set; } = 5;
    public int CommandMaxAttempts { get; set; } = 8;
    public List<Events.IfoodPollingMerchant> CommandMerchants { get; set; } = new();
    public List<string> AllowedCommands { get; set; } = new();
    public bool ScheduledConfirmationUsesPreparationStart { get; set; }
    public bool ExceptionsEnabled { get; set; }
    public int ExceptionBatchSize { get; set; } = 5;
    public int CancellationReasonsTtlSeconds { get; set; } = 120;
    public List<Events.IfoodPollingMerchant> ExceptionMerchants { get; set; } = new();
    public List<string> AllowedExceptionActions { get; set; } = new();
    public int ImportLeaseSeconds { get; set; } = 90;
    public int ImportBatchSize { get; set; } = 5;
    public int ImportMaxAttempts { get; set; } = 8;
    public int PollingLeaseSeconds { get; set; } = 90;
    public int AckBatchSize { get; set; } = 2000;
    public List<Events.IfoodPollingMerchant> PollingMerchants { get; set; } = new();
    public string ActiveEncryptionKeyId { get; set; } = "";
    [JsonIgnore] public Dictionary<string, string> EncryptionKeys { get; set; } = new();
    public override string ToString() => $"iFood (enabled={Enabled}; credenciais omitidas)";
}

public sealed class IfoodOptionsValidator : IValidateOptions<IfoodOptions>
{
    public ValidateOptionsResult Validate(string? name, IfoodOptions options)
    {
        if (!options.Enabled) return ValidateOptionsResult.Success;
        var errors = new List<string>();
        if (!ValidCredential(options.ClientId) ||
            (!ValidCredential(options.ClientSecret) && string.IsNullOrEmpty(options.ClientSecretCifrado)))
            errors.Add("Ifood:ClientId e ClientSecret (ou ClientSecretCifrado) devem ser configurados.");
        if (options.HttpTimeoutSeconds is < 1 or > 120 || options.RenewalSkewSeconds is < 0 or > 300 ||
            options.FailureCooldownSeconds is < 1 or > 300)
            errors.Add("Timeout, margem de renovação ou pausa de autenticação iFood inválidos.");
        if (!ValidKeyId(options.ActiveEncryptionKeyId) ||
            !options.EncryptionKeys.ContainsKey(options.ActiveEncryptionKeyId))
            errors.Add("A chave ativa iFood deve existir em EncryptionKeys.");
        foreach (var key in options.EncryptionKeys)
        {
            if (!ValidKeyId(key.Key) || !ValidEncryptionKey(key.Value))
                errors.Add("As chaves iFood devem ter identificador válido e 32 bytes em Base64.");
        }
        if (options.PollingEnabled && (options.PollingMerchants.Count is < 1 or > 100 ||
            options.PollingMerchants.Any(m => m.MerchantId == Guid.Empty || m.EstabelecimentoId == Guid.Empty) ||
            options.PollingMerchants.Select(m => m.MerchantId).Distinct().Count() != options.PollingMerchants.Count ||
            options.PollingLeaseSeconds < options.HttpTimeoutSeconds + 10 || options.PollingLeaseSeconds > 600 ||
            options.AckBatchSize is < 1 or > 2000))
            errors.Add("Polling iFood requer 1 a 100 merchants únicos, lojas válidas, reserva maior que timeout e ACK de até 2000 eventos.");
        if (options.ImportEnabled && (options.ImportLeaseSeconds < options.HttpTimeoutSeconds * 2 + 10 ||
            options.ImportLeaseSeconds > 600 || options.ImportBatchSize is < 1 or > 50 || options.ImportMaxAttempts is < 1 or > 100))
            errors.Add("Importação iFood requer reserva superior a dois timeouts, lote de 1 a 50 e tentativas de 1 a 100.");
        if (options.ProjectionEnabled && (options.ProjectionLeaseSeconds is < 10 or > 600 ||
            options.ProjectionBatchSize is < 1 or > 100 || options.ProjectionMaxAttempts is < 1 or > 100))
            errors.Add("Projeção iFood requer reserva de 10 a 600 segundos, lote de 1 a 100 e tentativas de 1 a 100.");
        if (options.CommandsEnabled && (options.CommandLeaseSeconds < 2 * options.HttpTimeoutSeconds + 10 || options.CommandLeaseSeconds > 600 ||
            options.CommandBatchSize is < 1 or > 50 || options.CommandMaxAttempts is < 1 or > 100 ||
            options.CommandMerchants.Count is < 1 or > 100 || options.CommandMerchants.Any(m => m.MerchantId == Guid.Empty || m.EstabelecimentoId == Guid.Empty) ||
            options.CommandMerchants.Select(m => m.MerchantId).Distinct().Count() != options.CommandMerchants.Count ||
            options.AllowedCommands.Count is < 1 or > 4 || options.AllowedCommands.Distinct().Count() != options.AllowedCommands.Count ||
            options.AllowedCommands.Any(c => !Commands.IfoodCommandRules.IsKnown(c))))
            errors.Add("Comandos iFood requerem merchants/lojas explícitos, comandos habilitados, reserva superior a dois timeouts e limites válidos.");
        if (options.ExceptionsEnabled && (options.CommandLeaseSeconds < 3 * options.HttpTimeoutSeconds + 10 || options.CommandLeaseSeconds > 600 ||
            options.CommandMaxAttempts is < 1 or > 100 || options.ExceptionBatchSize is < 1 or > 50 || options.CancellationReasonsTtlSeconds is < 15 or > 300 ||
            options.ExceptionMerchants.Count is < 1 or > 100 || options.ExceptionMerchants.Any(m=>m.MerchantId==Guid.Empty || m.EstabelecimentoId==Guid.Empty) ||
            options.ExceptionMerchants.Select(m=>m.MerchantId).Distinct().Count()!=options.ExceptionMerchants.Count ||
            options.AllowedExceptionActions.Count is < 1 or > 4 || options.AllowedExceptionActions.Distinct().Count()!=options.AllowedExceptionActions.Count ||
            options.AllowedExceptionActions.Any(c=>!Exceptions.IfoodExceptionRules.IsKnown(c))))
            errors.Add("Cancelamento/disputas iFood requerem merchants/ações explícitos, reserva de três timeouts, limites e validade dos motivos configurados.");
        if (options.RetentionEnabled && options.RetentionDays is < 7 or > 365)
            errors.Add("A retenção iFood deve ficar entre 7 e 365 dias.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static bool ValidCredential(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value != "__SET_IN_ENV__" && value.Length <= 8000 && !value.Any(char.IsControl);
    internal static bool ValidKeyId(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 64 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static bool ValidEncryptionKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { return Convert.FromBase64String(value).Length == 32; }
        catch (FormatException) { return false; }
    }
}
