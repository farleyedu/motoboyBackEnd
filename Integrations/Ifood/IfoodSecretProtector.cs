using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace APIBack.Integrations.Ifood;

// Envelope versionado com key ID e propósito autenticados: permite manter chaves antigas na rotação.
public sealed class IfoodSecretProtector
{
    private readonly IfoodOptions _options;
    public IfoodSecretProtector(IOptions<IfoodOptions> options) => _options = options.Value;

    public string Protect(string secret, string purpose)
    {
        EnsureEnabled();
        if (string.IsNullOrEmpty(secret) || secret.Length > 8000 || !IfoodOptionsValidator.ValidKeyId(purpose))
            throw new IfoodIntegrationException("IFOOD_SECRET_INVALID_INPUT");
        var id = _options.ActiveEncryptionKeyId;
        var key = GetKey(id);
        var plaintext = Encoding.UTF8.GetBytes(secret);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(id, purpose));
            return $"v1.{id}.{Convert.ToBase64String(nonce.Concat(tag).Concat(ciphertext).ToArray())}";
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext); }
    }

    public string Unprotect(string envelope, string purpose)
    {
        EnsureEnabled();
        if (string.IsNullOrEmpty(envelope) || envelope.Length > 44000 || !IfoodOptionsValidator.ValidKeyId(purpose))
            throw new IfoodIntegrationException("IFOOD_SECRET_INVALID_ENVELOPE");
        var parts = envelope.Split('.', 3);
        if (parts.Length != 3 || parts[0] != "v1") throw new IfoodIntegrationException("IFOOD_SECRET_INVALID_ENVELOPE");
        var key = GetKey(parts[1]);
        byte[]? plaintext = null;
        try
        {
            var payload = Convert.FromBase64String(parts[2]);
            if (payload.Length < 29) throw new IfoodIntegrationException("IFOOD_SECRET_INVALID_ENVELOPE");
            plaintext = new byte[payload.Length - 28];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(payload.AsSpan(0, 12), payload.AsSpan(28), payload.AsSpan(12, 16),
                plaintext, AssociatedData(parts[1], purpose));
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        { throw new IfoodIntegrationException("IFOOD_SECRET_INVALID_ENVELOPE"); }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private byte[] GetKey(string id)
    {
        if (!_options.EncryptionKeys.TryGetValue(id, out var encoded))
            throw new IfoodIntegrationException("IFOOD_SECRET_KEY_UNAVAILABLE");
        try
        {
            var key = Convert.FromBase64String(encoded);
            if (key.Length == 32) return key;
            CryptographicOperations.ZeroMemory(key);
        }
        catch (FormatException) { }
        throw new IfoodIntegrationException("IFOOD_SECRET_KEY_UNAVAILABLE");
    }
    private void EnsureEnabled()
    { if (!_options.Enabled) throw new IfoodIntegrationException("IFOOD_DISABLED"); }
    private static byte[] AssociatedData(string id, string purpose) => Encoding.UTF8.GetBytes($"ifood:v1:{id}:{purpose}");
}
