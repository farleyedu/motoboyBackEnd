using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace APIBack.Security
{
    public static class RoleCatalog
    {
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["funcionario"] = "funcionario",
            ["atendente"] = "atendente",
            ["atendente_whatsapp"] = "atendente",
            ["agente"] = "atendente",
            ["cabeleireiro"] = "profissional",
            ["barbeiro"] = "profissional",
            ["profissional"] = "profissional",
            ["vendedor"] = "vendedor",
            ["motoboy"] = "motoboy",
            ["admin"] = "gerente_empresa",
            ["gerente"] = "gerente_estabelecimento",
            ["gerente_empresa"] = "gerente_empresa",
            ["gerente_estabelecimento"] = "gerente_estabelecimento",
            ["dono"] = "dono",
            ["colaborador"] = "colaborador",
            ["super_admin"] = "super_admin"
        };

        private static readonly Dictionary<string, string[]> AllowedRolesByEstablishmentType =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["barbearia"] = new[] { "gerente_estabelecimento", "profissional", "atendente", "funcionario" },
                ["clinica"] = new[] { "gerente_estabelecimento", "profissional", "atendente", "funcionario" },
                ["garagem"] = new[] { "gerente_estabelecimento", "vendedor", "atendente", "funcionario" },
                ["hotel"] = new[] { "gerente_estabelecimento", "atendente", "funcionario" },
                ["restaurante"] = new[] { "gerente_estabelecimento", "atendente", "motoboy", "funcionario" }
            };

        public static string Normalize(string? role)
        {
            if (string.IsNullOrWhiteSpace(role))
            {
                return string.Empty;
            }

            var key = NormalizeToken(role);
            return Aliases.TryGetValue(key, out var normalized)
                ? normalized
                : key;
        }

        public static int Rank(string? role, bool isSuperAdmin)
        {
            if (isSuperAdmin)
            {
                return 1000;
            }

            return Normalize(role) switch
            {
                "dono" => 950,
                "gerente_empresa" => 900,
                "gerente_estabelecimento" => 800,
                "atendente" => 600,
                "profissional" => 600,
                "vendedor" => 600,
                "motoboy" => 600,
                "funcionario" => 500,
                "colaborador" => 300,
                _ => 100
            };
        }

        public static bool CanManagePermissions(string? role, bool isSuperAdmin)
        {
            if (isSuperAdmin)
            {
                return true;
            }

            var normalized = Normalize(role);
            return string.Equals(normalized, "dono", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "gerente_empresa", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "gerente_estabelecimento", StringComparison.OrdinalIgnoreCase);
        }

        public static bool CanViewUsers(string? companyRole, string? establishmentRole, bool isSuperAdmin)
        {
            if (isSuperAdmin)
            {
                return true;
            }

            var normalizedCompanyRole = Normalize(companyRole);
            var normalizedEstablishmentRole = Normalize(establishmentRole);

            return string.Equals(normalizedCompanyRole, "dono", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedCompanyRole, "gerente_empresa", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedEstablishmentRole, "gerente_estabelecimento", StringComparison.OrdinalIgnoreCase);
        }

        public static bool CanCreateUsers(string? companyRole, string? establishmentRole, bool isSuperAdmin)
            => CanViewUsers(companyRole, establishmentRole, isSuperAdmin);

        public static bool CanCreateManagers(bool isSuperAdmin) => isSuperAdmin;

        public static string ResolveScreenRole(string? companyRole, string? establishmentRole, bool isSuperAdmin)
        {
            if (isSuperAdmin)
            {
                return "super_admin";
            }

            var normalizedCompanyRole = Normalize(companyRole);
            if (string.Equals(normalizedCompanyRole, "dono", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedCompanyRole, "gerente_empresa", StringComparison.OrdinalIgnoreCase))
            {
                return normalizedCompanyRole;
            }

            var normalizedEstablishmentRole = Normalize(establishmentRole);
            if (string.Equals(normalizedEstablishmentRole, "gerente_estabelecimento", StringComparison.OrdinalIgnoreCase))
            {
                return normalizedEstablishmentRole;
            }

            return "usuario_comum";
        }

        public static IReadOnlyCollection<string> GetAllowedTargetUserTypes(string? establishmentType)
        {
            if (string.IsNullOrWhiteSpace(establishmentType))
            {
                return Array.Empty<string>();
            }

            return AllowedRolesByEstablishmentType.TryGetValue(NormalizeToken(establishmentType), out var roles)
                ? roles
                : Array.Empty<string>();
        }

        public static IReadOnlyCollection<string> GetAllowedManagerTypes(bool isSuperAdmin)
        {
            if (!isSuperAdmin)
            {
                return Array.Empty<string>();
            }

            return new[] { "gerente_estabelecimento", "gerente_empresa" };
        }

        public static bool IsCompanyRole(string? role)
        {
            var normalized = Normalize(role);
            return string.Equals(normalized, "dono", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "gerente_empresa", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "colaborador", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsManagerRole(string? role)
        {
            var normalized = Normalize(role);
            return string.Equals(normalized, "dono", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "gerente_empresa", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "gerente_estabelecimento", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsAllowedForEstablishmentType(string? role, string? establishmentType)
        {
            var normalized = Normalize(role);
            if (string.Equals(normalized, "gerente_empresa", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return GetAllowedTargetUserTypes(establishmentType)
                .Any(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);
            var lastWasSeparator = false;

            foreach (var ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(ch))
                {
                    builder.Append(ch);
                    lastWasSeparator = false;
                    continue;
                }

                if (ch == '_' || ch == '-' || char.IsWhiteSpace(ch))
                {
                    if (!lastWasSeparator && builder.Length > 0)
                    {
                        builder.Append('_');
                        lastWasSeparator = true;
                    }
                }
            }

            return builder.ToString().Trim('_').Normalize(NormalizationForm.FormC);
        }
    }
}
