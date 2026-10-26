using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace APIBack.Payments.MercadoPago
{
    /// <summary>
    /// Cifra segredos de pagamento (tokens OAuth, client secret, segredo do webhook) com AES-256-GCM. A chave mestra vem de
    /// <c>Payments:ChaveMestra</c> (env <c>Payments__ChaveMestra</c>) ou <c>PAGAMENTOS_CHAVE_MESTRA</c>: 32 bytes em base64, nunca no banco.
    /// O <paramref name="contexto"/> entra como dado associado: um cifrado copiado para outra linha/coluna nao abre.
    /// </summary>
    public interface IPagamentoSegredoProtector
    {
        bool Configurado { get; }
        string Proteger(string segredo, string contexto);
        /// <summary>Null quando o cifrado nao abre (chave errada, contexto errado, adulterado).</summary>
        string? Revelar(string? cifrado, string contexto);
    }

    /// <summary>Contextos (AAD) padronizados. Quem grava e quem le usam os mesmos.</summary>
    public static class SegredoContexto
    {
        public static string AplicacaoClientSecret(Guid id) => $"mp_aplicacao:client_secret:{id:N}";
        public static string AplicacaoWebhookSecret(Guid id) => $"mp_aplicacao:webhook_secret:{id:N}";
        public static string ZippyAccessToken(Guid id) => $"mp_conta_zippy:access_token:{id:N}";
        public static string LojaAccessToken(Guid id) => $"mp_conta_loja:access_token:{id:N}";
        public static string LojaRefreshToken(Guid id) => $"mp_conta_loja:refresh_token:{id:N}";
        public static string OAuthCodeVerifier(Guid id) => $"mp_oauth_tentativa:code_verifier:{id:N}";
    }

    public sealed class PagamentoSegredoProtector : IPagamentoSegredoProtector
    {
        private const string Prefixo = "pg1.";
        private readonly byte[]? _chave;
        private readonly byte[]? _chaveAnterior;

        public PagamentoSegredoProtector(IConfiguration configuration)
        {
            var configurada = configuration["Payments:ChaveMestra"];
            _chave = LerChave(string.IsNullOrWhiteSpace(configurada) ? Environment.GetEnvironmentVariable("PAGAMENTOS_CHAVE_MESTRA") : configurada);
            // Rotacao: o anterior so abre; tudo que for regravado sai com a chave nova.
            _chaveAnterior = LerChave(configuration["Payments:ChaveMestraAnterior"]);
        }

        internal PagamentoSegredoProtector(byte[] chave, byte[]? anterior = null)
        {
            _chave = chave;
            _chaveAnterior = anterior;
        }

        public bool Configurado => _chave != null;

        public string Proteger(string segredo, string contexto)
        {
            if (_chave == null) throw PagamentoException.NaoConfigurado("Chave mestra de pagamentos nao configurada.");
            if (string.IsNullOrEmpty(segredo)) throw new ArgumentException("Segredo vazio.", nameof(segredo));

            var nonce = RandomNumberGenerator.GetBytes(12);
            var texto = Encoding.UTF8.GetBytes(segredo);
            var cifrado = new byte[texto.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(_chave, 16))
            {
                aes.Encrypt(nonce, texto, cifrado, tag, Encoding.UTF8.GetBytes(contexto));
            }

            var junto = new byte[nonce.Length + tag.Length + cifrado.Length];
            Buffer.BlockCopy(nonce, 0, junto, 0, nonce.Length);
            Buffer.BlockCopy(tag, 0, junto, nonce.Length, tag.Length);
            Buffer.BlockCopy(cifrado, 0, junto, nonce.Length + tag.Length, cifrado.Length);
            return Prefixo + Convert.ToBase64String(junto);
        }

        public string? Revelar(string? cifrado, string contexto)
        {
            if (string.IsNullOrWhiteSpace(cifrado) || !cifrado.StartsWith(Prefixo, StringComparison.Ordinal)) return null;
            return Abrir(_chave, cifrado, contexto) ?? Abrir(_chaveAnterior, cifrado, contexto);
        }

        private static string? Abrir(byte[]? chave, string cifrado, string contexto)
        {
            if (chave == null) return null;
            try
            {
                var junto = Convert.FromBase64String(cifrado[Prefixo.Length..]);
                if (junto.Length < 28) return null;
                var texto = new byte[junto.Length - 28];
                using var aes = new AesGcm(chave, 16);
                aes.Decrypt(junto.AsSpan(0, 12), junto.AsSpan(28), junto.AsSpan(12, 16), texto, Encoding.UTF8.GetBytes(contexto));
                return Encoding.UTF8.GetString(texto);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                return null;
            }
        }

        /// <summary>So base64 de 32 bytes: frase curta como chave de dinheiro nao passa.</summary>
        private static byte[]? LerChave(string? texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return null;
            try
            {
                var bytes = Convert.FromBase64String(texto.Trim());
                return bytes.Length == 32 ? bytes : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
