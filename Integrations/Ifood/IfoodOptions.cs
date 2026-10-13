using Microsoft.Extensions.Options;
using System.Text.Json.Serialization;

namespace APIBack.Integrations.Ifood;

public sealed class IfoodOptions
{
    public const string SectionName = "Ifood";
    public bool Enabled { get; set; }
    public string ClientId { get; set; } = "";
    [JsonIgnore] public string ClientSecret { get; set; } = "";
    public int HttpTimeoutSeconds { get; set; } = 15;
    public int RenewalSkewSeconds { get; set; } = 60;
    public int FailureCooldownSeconds { get; set; } = 10;
    public bool PollingEnabled { get; set; }
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
        if (!ValidCredential(options.ClientId) || !ValidCredential(options.ClientSecret))
            errors.Add("Ifood:ClientId e ClientSecret devem ser configurados no servidor.");
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
