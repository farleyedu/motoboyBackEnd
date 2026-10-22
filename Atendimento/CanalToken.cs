using System;
using APIBack.Automation.Interfaces;

namespace APIBack.Atendimento
{
    /// <summary>De onde sai o token usado para falar com a Meta em nome de um numero.</summary>
    public static class CanalToken
    {
        public const string DoCanal = "canal";
        public const string Global = "global";

        /// <summary>
        /// O token do proprio numero; so quem e da Zippy (origem zippy ou central) ou ainda nao tem canal cai, de forma explicita,
        /// no token global (WhatsApp__AccessToken). O numero proprio do cliente NUNCA usa o token global: seria a conta Meta da Zippy
        /// falando por um numero que nao e dela.
        /// </summary>
        public static (string? Token, string Origem) Resolver(CanalWhatsapp? canal, ITokenProtector protetor, IWhatsAppTokenProvider global)
        {
            var doCanal = protetor.Revelar(canal?.TokenCifrado);
            if (!string.IsNullOrWhiteSpace(doCanal)) return (doCanal, DoCanal);
            if (canal?.Origem == OrigemCanal.Proprio) return (null, DoCanal);

            var token = global.GetAccessToken();
            return (TokenGlobalValido(token) ? token : null, Global);
        }

        /// <summary>Os valores de modelo do appsettings ("__SET_IN_ENV__") contam como token ausente.</summary>
        public static bool TokenGlobalValido(string? token) =>
            !string.IsNullOrWhiteSpace(token) && !token.TrimStart().StartsWith("__", StringComparison.Ordinal);
    }
}
