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
            .Where(p => Administrative.Contains(p.Key) || active.Contains(p.Key) || IsOrderOperation(p.Key, active))
            .ToDictionary(p => p.Key, p => StripActionsOfInactiveModules(p.Key, p.Value, active), StringComparer.OrdinalIgnoreCase);
    }

    public static Dictionary<string, List<string>> ResolveForAccess(string? json, string[]? modules,
        string? companyRole, string? establishmentRole)
    {
        var normalizedCompanyRole = RoleCatalog.Normalize(companyRole);
        var normalizedEstablishmentRole = RoleCatalog.Normalize(establishmentRole);
        var isCompanyManager = normalizedCompanyRole is "dono" or "gerente_empresa";
        var isEstablishmentManager = normalizedEstablishmentRole == "gerente_estabelecimento";

        if (!isCompanyManager && !isEstablishmentManager)
            return Resolve(json, modules);

        var active = EstabelecimentoModuleMapper.ToUiModules(string.Empty, modules)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var catalog = Catalog();
        var permissions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in catalog)
        {
            var allowed = string.Equals(entry.Key, "Configuracoes", StringComparison.OrdinalIgnoreCase)
                || active.Contains(entry.Key)
                || IsOrderOperation(entry.Key, active)
                || (isCompanyManager && Administrative.Contains(entry.Key));
            if (!allowed) continue;

            permissions[entry.Key] = StripActionsOfInactiveModules(entry.Key, entry.Value.ToList(), active);
        }

        return permissions;
    }

    // A permissao "Delivery" e a da operacao de pedidos (ver, criar, editar, imprimir, cancelar): vale para toda loja com
    // Pedidos, mesmo sem o modulo Delivery (planos Partida e Rota). O que e de motoboy fica travado por [RequireModule("Delivery")].
    private static bool IsOrderOperation(string modulo, HashSet<string> active) =>
        string.Equals(modulo, "Delivery", StringComparison.OrdinalIgnoreCase) && active.Contains("Pedidos");

    // Acoes que moram sob a permissao "Delivery", mas pertencem a um modulo comercial proprio (plano de modulos, 3.3):
    // com o modulo desligado, a acao some e os [RequirePermission] respondem 403.
    private static readonly (string Modulo, string Acao, string ModuloExigido)[] ActionModules =
    {
        ("Delivery", "gestao_motoboy", "Delivery"),
        ("Delivery", "integracao_ifood", "iFood"),
        ("Delivery", "configurar_impressao", "Impressao"),
    };

    private static List<string> StripActionsOfInactiveModules(string modulo, List<string> acoes, HashSet<string> active)
    {
        var blocked = ActionModules
            .Where(r => string.Equals(r.Modulo, modulo, StringComparison.OrdinalIgnoreCase) && !active.Contains(r.ModuloExigido))
            .Select(r => r.Acao)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return blocked.Count == 0 ? acoes : acoes.Where(a => !blocked.Contains(a)).ToList();
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
        // "configurar" do WhatsApp (conectar/desconectar o numero da loja) e checado no CanaisWhatsappService, sem atributo.
        result["WhatsApp"] = result.GetValueOrDefault("WhatsApp", Array.Empty<string>())
            .Append("configurar").Distinct().OrderBy(a => a).ToArray();
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
