using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace APIBack.Atendimento
{
    /// <summary>Cifra o token da Meta de cada numero antes de ir ao banco (AES-GCM, chave mestra em variavel de ambiente).</summary>
    public interface ITokenProtector
    {
        bool Configurado { get; }
        string Proteger(string token);
        string? Revelar(string? cifrado);
    }

    public sealed class TokenProtector : ITokenProtector
    {
        private const string Prefixo = "v1.";
        private readonly byte[]? _chave;

        public TokenProtector(IConfiguration configuration)
        {
            var texto = configuration["Atendimento:ChaveToken"];
            if (string.IsNullOrWhiteSpace(texto)) return;

            // Aceita uma chave base64 de 32 bytes ou qualquer frase (vira 32 bytes por SHA-256).
            try
            {
                var bytes = Convert.FromBase64String(texto);
                _chave = bytes.Length == 32 ? bytes : SHA256.HashData(Encoding.UTF8.GetBytes(texto));
            }
            catch (FormatException)
            {
                _chave = SHA256.HashData(Encoding.UTF8.GetBytes(texto));
            }
        }

        public bool Configurado => _chave != null;

        public string Proteger(string token)
        {
            if (_chave == null) throw new InvalidOperationException("Atendimento:ChaveToken nao configurada.");

            var nonce = RandomNumberGenerator.GetBytes(12);
            var texto = Encoding.UTF8.GetBytes(token);
            var cifrado = new byte[texto.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(_chave, 16);
            aes.Encrypt(nonce, texto, cifrado, tag);

            var junto = new byte[nonce.Length + tag.Length + cifrado.Length];
            Buffer.BlockCopy(nonce, 0, junto, 0, nonce.Length);
            Buffer.BlockCopy(tag, 0, junto, nonce.Length, tag.Length);
            Buffer.BlockCopy(cifrado, 0, junto, nonce.Length + tag.Length, cifrado.Length);
            return Prefixo + Convert.ToBase64String(junto);
        }

        public string? Revelar(string? cifrado)
        {
            if (string.IsNullOrWhiteSpace(cifrado) || _chave == null || !cifrado.StartsWith(Prefixo, StringComparison.Ordinal)) return null;

            try
            {
                var junto = Convert.FromBase64String(cifrado[Prefixo.Length..]);
                var nonce = junto[..12];
                var tag = junto[12..28];
                var dados = junto[28..];
                var texto = new byte[dados.Length];
                using var aes = new AesGcm(_chave, 16);
                aes.Decrypt(nonce, dados, tag, texto);
                return Encoding.UTF8.GetString(texto);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                return null;
            }
        }
    }
}
