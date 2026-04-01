using System;
using System.Collections.Generic;
using System.Linq;

namespace APIBack.Service
{
    internal static class CardapioPermissionBridge
    {
        private static readonly string[] CardapioActions = { "visualizar", "criar", "editar", "excluir" };
        private static readonly string[] CardapioWebActions = { "configurar", "publicar" };

        public static Dictionary<string, List<string>> Apply(
            Dictionary<string, List<string>>? source,
            IReadOnlyCollection<string>? modulosAtivos)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in source ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase))
            {
                result[item.Key] = (item.Value ?? new List<string>())
                    .Where(action => !string.IsNullOrWhiteSpace(action))
                    .Select(action => action.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var activeModules = (modulosAtivos ?? Array.Empty<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (activeModules.Contains("Cardapio"))
            {
                EnsureCardapioPermissions(result);
            }

            if (activeModules.Contains("CardapioWeb"))
            {
                EnsureCardapioWebPermissions(result);
            }

            return result;
        }

        private static void EnsureCardapioPermissions(Dictionary<string, List<string>> permissions)
        {
            if (permissions.ContainsKey("Cardapio"))
            {
                NormalizeActions(permissions["Cardapio"], CardapioActions);
                return;
            }

            if (!permissions.TryGetValue("Configuracoes", out var configActions))
            {
                return;
            }

            var actions = new List<string>();
            if (HasAction(configActions, "visualizar"))
            {
                actions.Add("visualizar");
            }

            if (HasAction(configActions, "criar"))
            {
                actions.Add("criar");
            }

            if (HasAction(configActions, "editar"))
            {
                actions.Add("editar");
            }

            if (HasAction(configActions, "deletar") || HasAction(configActions, "excluir"))
            {
                actions.Add("excluir");
            }

            if (actions.Count > 0)
            {
                permissions["Cardapio"] = actions;
            }
        }

        private static void EnsureCardapioWebPermissions(Dictionary<string, List<string>> permissions)
        {
            if (permissions.ContainsKey("CardapioWeb"))
            {
                NormalizeActions(permissions["CardapioWeb"], CardapioWebActions);
                return;
            }

            if (!permissions.TryGetValue("Configuracoes", out var configActions))
            {
                return;
            }

            var actions = new List<string>();
            if (HasAction(configActions, "configurar") || HasAction(configActions, "editar"))
            {
                actions.Add("configurar");
            }

            if (HasAction(configActions, "configurar") || HasAction(configActions, "editar"))
            {
                actions.Add("publicar");
            }

            if (actions.Count > 0)
            {
                permissions["CardapioWeb"] = actions;
            }
        }

        private static void NormalizeActions(List<string> actions, IReadOnlyCollection<string> allowed)
        {
            if (actions == null)
            {
                return;
            }

            var normalized = actions
                .Where(action => !string.IsNullOrWhiteSpace(action))
                .Select(action => action.Trim())
                .Select(action => string.Equals(action, "deletar", StringComparison.OrdinalIgnoreCase) ? "excluir" : action)
                .Where(action => allowed.Contains(action, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            actions.Clear();
            actions.AddRange(normalized);
        }

        private static bool HasAction(IEnumerable<string>? actions, string action)
        {
            return (actions ?? Array.Empty<string>())
                .Any(item => string.Equals(item?.Trim(), action, StringComparison.OrdinalIgnoreCase));
        }
    }
}
