using System.Reflection;
using System.Text.Json;
using APIBack.Attributes;
using APIBack.Model.Gestao;
using APIBack.Service;

namespace APIBack.Security;

public static class EstablishmentPermissions
{
    // Administrative capabilities are independent from commercial module activation.
    private static readonly HashSet<string> Administrative = new(StringComparer.OrdinalIgnoreCase)
        { "Configuracoes", "Usuarios", "Estabelecimentos", "Empresas" };

    public static Dictionary<string, List<string>> Resolve(string? json, string[]? modules)
    {
        Dictionary<string, List<string>> permissions;
        try
        {
            permissions = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json ?? "{}")
                ?? new();
        }
        catch (JsonException) { permissions = new(); }
        var active = EstabelecimentoModuleMapper.ToUiModules(string.Empty, modules)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return CardapioPermissionBridge.Apply(permissions, active.ToArray())
            .Where(p => Administrative.Contains(p.Key) || active.Contains(p.Key))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
    }

    public static Dictionary<string, string[]> Catalog()
    {
        var attributes = typeof(RequirePermissionAttribute).Assembly.GetTypes()
            .Where(t => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetCustomAttributes<RequirePermissionAttribute>()
                .Concat(t.GetMethods().SelectMany(m => m.GetCustomAttributes<RequirePermissionAttribute>())));
        var result = attributes.GroupBy(a => a.Modulo, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(a => a.Acao).Distinct().OrderBy(a => a).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        result["Financeiro"] = new[] { "visualizar", "criar", "editar", "cancelar", "exportar", "configurar" };
        foreach (var module in new[] { "Usuarios", "Empresas", "Estabelecimentos" })
            result[module] = new[] { "visualizar", "criar", "editar", "excluir", "deletar" };
        return result;
    }

    public static string? Validate(Dictionary<string, List<string>> permissions,
        Dictionary<string, List<string>> actor, bool superAdmin,
        Dictionary<string, List<string>>? previous = null)
    {
        var catalog = Catalog();
        foreach (var entry in permissions)
        {
            var existing = previous?.FirstOrDefault(p => string.Equals(p.Key, entry.Key, StringComparison.OrdinalIgnoreCase)).Value ?? new();
            var additions = entry.Value?.Except(existing, StringComparer.OrdinalIgnoreCase).ToArray();
            if (additions == null || (additions.Length > 0 && (!catalog.TryGetValue(entry.Key, out var allowed) ||
                additions.Any(action => !allowed.Contains(action, StringComparer.OrdinalIgnoreCase)))))
                return "Módulo ou ação de permissão inválido.";
            if (!superAdmin && additions.Any(action => !actor.Any(p =>
                string.Equals(p.Key, entry.Key, StringComparison.OrdinalIgnoreCase) &&
                p.Value.Contains(action, StringComparer.OrdinalIgnoreCase))))
                return "Você não pode conceder uma permissão que não possui.";
        }
        return null;
    }
}
