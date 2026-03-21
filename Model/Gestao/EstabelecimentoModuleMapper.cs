using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace APIBack.Model.Gestao
{
    internal static class EstabelecimentoModuleMapper
    {
        public static string[] ToDatabaseModules(IEnumerable<string>? moduleNames, string? establishmentTypeSlug)
        {
            var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GERAL" };

            foreach (var moduleName in moduleNames ?? Array.Empty<string>())
            {
                switch (NormalizeToken(moduleName))
                {
                    case "delivery":
                        modules.Add("DELIVERY");
                        break;
                    case "whatsapp":
                        modules.Add("WHATSAPP");
                        break;
                    case "reservas":
                    case "agendamentos":
                    case "reserva":
                        modules.Add("RESERVA");
                        break;
                    case "garagem":
                        modules.Add("GARAGEM");
                        break;
                    case "nautica":
                        modules.Add("NAUTICA");
                        break;
                    case "barbearia":
                        modules.Add("BARBEARIA");
                        break;
                    case "estabelecimentos":
                    case "estabelecimento":
                        modules.Add("ESTABELECIMENTO");
                        break;
                    case "usuarios":
                    case "usuario":
                        modules.Add("USUARIOS");
                        break;
                    case "empresas":
                    case "empresa":
                        modules.Add("EMPRESAS");
                        break;
                    case "configuracoes":
                    case "configuracao":
                        modules.Add("CONFIGURACOES");
                        break;
                }
            }

            ApplyOperationalModulePolicy(modules, establishmentTypeSlug);
            return modules.ToArray();
        }

        public static List<string> ToUiModules(string establishmentName, string[]? rawModules)
        {
            var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var moduleName in rawModules ?? Array.Empty<string>())
            {
                switch (NormalizeToken(moduleName))
                {
                    case "delivery":
                        modules.Add("Delivery");
                        break;
                    case "whatsapp":
                        modules.Add("WhatsApp");
                        break;
                    case "reserva":
                        modules.Add("Reservas");
                        modules.Add("Agendamentos");
                        break;
                    case "garagem":
                        modules.Add("Garagem");
                        break;
                    case "nautica":
                        modules.Add("Nautica");
                        break;
                    case "barbearia":
                        modules.Add("Agendamentos");
                        break;
                    case "estabelecimento":
                        modules.Add("Estabelecimentos");
                        break;
                    case "usuarios":
                    case "usuario":
                        modules.Add("Usuarios");
                        break;
                    case "empresas":
                    case "empresa":
                        modules.Add("Empresas");
                        break;
                    case "configuracoes":
                    case "configuracao":
                        modules.Add("Configuracoes");
                        break;
                }
            }

            if (NormalizeToken(establishmentName).Contains("zippygo centro", StringComparison.Ordinal))
            {
                modules.Add("Usuarios");
                modules.Add("Empresas");
                modules.Add("Estabelecimentos");
                modules.Add("Configuracoes");
            }

            return modules.ToList();
        }

        private static void ApplyOperationalModulePolicy(HashSet<string> modules, string? establishmentTypeSlug)
        {
            if (string.Equals(NormalizeToken(establishmentTypeSlug), "nautica", StringComparison.Ordinal))
            {
                modules.Add("NAUTICA");
                return;
            }

            modules.Remove("NAUTICA");
        }

        private static string NormalizeToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
