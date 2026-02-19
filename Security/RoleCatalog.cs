using System;
using System.Collections.Generic;

namespace APIBack.Security
{
    public static class RoleCatalog
    {
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["funcionario"] = "funcionario",
            ["funcionário"] = "funcionario",
            ["atendente"] = "atendente",
            ["atendente_whatsapp"] = "atendente",
            ["cabeleireiro"] = "cabeleireiro",
            ["barbeiro"] = "cabeleireiro",
            ["motoboy"] = "motoboy",
            ["gerente"] = "gerente",
            ["dono"] = "dono",
            ["super_admin"] = "super_admin"
        };

        public static string Normalize(string? role)
        {
            if (string.IsNullOrWhiteSpace(role))
            {
                return string.Empty;
            }

            var key = role.Trim().ToLowerInvariant();
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
                "dono" => 900,
                "gerente" => 800,
                "atendente" => 600,
                "cabeleireiro" => 600,
                "motoboy" => 600,
                "funcionario" => 500,
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
                || string.Equals(normalized, "gerente", StringComparison.OrdinalIgnoreCase);
        }
    }
}
