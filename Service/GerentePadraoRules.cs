using System;
using System.Linq;

namespace APIBack.Service
{
    /// <summary>Regras puras do gerente que toda empresa nova ganha (login gerado, trocavel depois).</summary>
    public static class GerentePadraoRules
    {
        public const string Dominio = "zippy.com";
        private const int SlugMaximo = 40;

        public static string Nome(string? empresa) =>
            string.IsNullOrWhiteSpace(empresa) ? "Gerente" : $"Gerente {empresa.Trim()}";

        /// <summary>gerente.&lt;slug&gt;@zippy.com; a tentativa 2 em diante vira gerente.&lt;slug&gt;2@zippy.com.</summary>
        public static string Email(string? slug, int tentativa = 1)
        {
            var limpo = new string((slug ?? string.Empty).ToLowerInvariant().Where(c => (c is >= 'a' and <= 'z') || (c is >= '0' and <= '9') || c == '-').ToArray()).Trim('-');
            if (limpo.Length > SlugMaximo) limpo = limpo[..SlugMaximo].TrimEnd('-');
            if (limpo.Length == 0) limpo = "loja";
            return tentativa <= 1 ? $"gerente.{limpo}@{Dominio}" : $"gerente.{limpo}{tentativa}@{Dominio}";
        }
    }
}
