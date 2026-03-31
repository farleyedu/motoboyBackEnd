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
                    case "reserva":
                    case "agendamentos":
                    case "agendamento":
                        modules.Add("AGENDAMENTOS");
                        break;
                    case "leads":
                    case "lead":
                        modules.Add("LEADS");
                        break;
                    case "estoque":
                        modules.Add("ESTOQUE");
                        break;
                    case "vitrine":
                        modules.Add("VITRINE");
                        break;
                    case "cardapio":
                        modules.Add("CARDAPIO");
                        break;
                    case "cardapioweb":
                    case "cardapio_web":
                        modules.Add("CARDAPIOWEB");
                        break;
                    case "relatorios":
                    case "relatorio":
                        modules.Add("RELATORIOS");
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
                    case "servicos":
                    case "servico":
                        modules.Add("SERVICOS");
                        break;
                    case "faq":
                        modules.Add("FAQ");
                        break;
                    case "disponibilidade":
                    case "disponivel":
                        modules.Add("DISPONIBILIDADE");
                        break;
                }
            }

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
                    case "agendamentos":
                    case "agendamento":
                    case "reservas":
                    case "reserva":
                        modules.Add("Agendamentos");
                        break;
                    case "leads":
                    case "lead":
                        modules.Add("Leads");
                        break;
                    case "estoque":
                        modules.Add("Estoque");
                        break;
                    case "vitrine":
                        modules.Add("Vitrine");
                        break;
                    case "cardapio":
                        modules.Add("Cardapio");
                        break;
                    case "cardapioweb":
                    case "cardapio_web":
                        modules.Add("CardapioWeb");
                        break;
                    case "relatorios":
                    case "relatorio":
                        modules.Add("Relatorios");
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
                    case "servicos":
                    case "servico":
                        modules.Add("Servicos");
                        break;
                    case "faq":
                        modules.Add("FAQ");
                        break;
                    case "disponibilidade":
                    case "disponivel":
                        modules.Add("Disponibilidade");
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
