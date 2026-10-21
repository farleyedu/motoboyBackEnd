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
                    case "pedidos":
                    case "pedido":
                        modules.Add("PEDIDOS");
                        break;
                    case "clientes":
                    case "cliente":
                        modules.Add("CLIENTES");
                        break;
                    // "Atendimento" e o nome comercial; a chave tecnica continua WHATSAPP.
                    case "whatsapp":
                    case "atendimento":
                        modules.Add("WHATSAPP");
                        break;
                    case "cardapio":
                        modules.Add("CARDAPIO");
                        break;
                    case "cardapioweb":
                        modules.Add("CARDAPIOWEB");
                        break;
                    case "ifood":
                        modules.Add("IFOOD");
                        break;
                    case "impressao":
                        modules.Add("IMPRESSAO");
                        break;
                    case "remuneracao":
                        modules.Add("REMUNERACAO");
                        break;
                    case "simulador":
                        modules.Add("SIMULADOR");
                        break;
                    case "experience":
                    case "zippy experience":
                        modules.Add("EXPERIENCE");
                        break;
                    case "financeiro":
                        modules.Add("FINANCEIRO");
                        break;
                    // Leads, Estoque, Vitrine, Agendamentos e Relatorios foram removidos do produto (sem tela): ignorados.
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
                    case "inteligencia":
                    case "gestao e inteligencia":
                        modules.Add("INTELIGENCIA");
                        break;
                }
            }

            // Escolher um modulo obriga escolher os que ele exige (DELIVERY -> PEDIDOS etc.).
            return ModuleDependencies.Close(modules).ToArray();
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
                    case "pedidos":
                    case "pedido":
                        modules.Add("Pedidos");
                        break;
                    case "clientes":
                    case "cliente":
                        modules.Add("Clientes");
                        break;
                    case "whatsapp":
                        modules.Add("WhatsApp");
                        break;
                    case "cardapio":
                        modules.Add("Cardapio");
                        break;
                    case "cardapioweb":
                        modules.Add("CardapioWeb");
                        break;
                    case "ifood":
                        modules.Add("iFood");
                        break;
                    case "impressao":
                        modules.Add("Impressao");
                        break;
                    case "remuneracao":
                        modules.Add("Remuneracao");
                        break;
                    case "simulador":
                        modules.Add("Simulador");
                        break;
                    case "experience":
                        modules.Add("Experience");
                        break;
                    case "financeiro":
                        modules.Add("Financeiro");
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
                    case "inteligencia":
                    case "gestao e inteligencia":
                        modules.Add("Inteligencia");
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
