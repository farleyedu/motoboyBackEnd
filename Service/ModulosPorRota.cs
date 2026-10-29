using System;
using System.Linq;

namespace APIBack.Service;

/// <summary>
/// Qual modulo comercial cada familia de rotas da loja exige (nomes da UI, como em <c>EstabelecimentoModuleMapper.ToUiModules</c>).
/// Antes so 4 modulos eram exigidos no backend (por <c>[RequireModule]</c>); os demais so sumiam do menu e a API continuava aberta.
/// Esta tabela e a trava unica das familias que nao tem atributo. Regras puras (sem banco) para testar.
/// </summary>
public static class ModulosPorRota
{
    public const string Off = "off";
    public const string Warn = "warn";
    public const string Enforce = "enforce";
    public const string CodigoModuloInativo = "MODULO_INATIVO";

    /// <summary>Modo vindo da configuracao <c>ModuleEnforcement</c>; qualquer valor desconhecido cai em "warn" (so registra).</summary>
    public static string Modo(string? configurado) => (configurado ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        Off => Off,
        Enforce => Enforce,
        _ => Warn
    };

    /// <summary>Modulo exigido pela rota, ou null quando a rota nao pertence a um modulo (login, gestao, app do motoboy, publicas).</summary>
    public static string? Exigido(string? path)
    {
        var s = (path ?? string.Empty).ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries);
        // A lista de conversas do Atendimento (painel) mora fora de /api.
        if (s.Length >= 1 && s[0] == "conversas") return "WhatsApp";
        if (s.Length < 2 || s[0] != "api") return null;

        // Fora da tabela: o app do motoboy (token operacional, sem a lista de modulos da loja), acesso ao sistema, gestao Zippy,
        // curadoria Zippy (so super admin), rotas publicas/webhooks e a situacao comercial. Atendimento <-> motoboy (chat) e do Delivery, nao do Atendimento.
        if (Is(s, "api", "v2", "motoboys") || Is(s, "api", "motoboys") || Is(s, "api", "auth") || Is(s, "api", "me") ||
            Is(s, "api", "gestao") || Is(s, "api", "curadoria") || Is(s, "api", "admin") || Is(s, "api", "public") || Is(s, "api", "webhooks") ||
            Is(s, "api", "v2", "comercial") || Is(s, "api", "v2", "atendimento", "chat")) return null;

        if (Is(s, "api", "v2", "delivery", "ifood") || Is(s, "api", "v2", "ifood", "store")) return "iFood";
        if (Is(s, "api", "v2", "delivery", "impressao")) return "Impressao";
        if (Is(s, "api", "v2", "delivery", "rider-work")) return "Remuneracao";
        if (Is(s, "api", "v2", "delivery", "cardapio-pedidos")) return "CardapioWeb";
        if (Is(s, "api", "v2", "simulador") || Is(s, "api", "delivery", "simulator")) return "Simulador";
        if (Is(s, "api", "v2", "delivery") || Is(s, "api", "pedido")) return "Pedidos";
        if (Is(s, "api", "experience")) return "Experience";
        if (Is(s, "api", "v2", "cardapio")) return "Cardapio";
        if (Is(s, "api", "v2", "clientes")) return "Clientes";
        if (Is(s, "api", "v2", "inteligencia")) return "Inteligencia";
        if (Is(s, "api", "crm")) return "Financeiro";
        if (Is(s, "api", "v2", "atendimento")) return "WhatsApp";
        if (Is(s, "api", "cardapio"))
            return s.Length > 2 && s[2] == "web" || s.Contains("atencao-motoboy") ? null : "Cardapio";
        if (Is(s, "api", "configuracoes"))
        {
            if (s.Contains("servicos")) return "Servicos";
            if (s.Contains("faqs") || s.Contains("faq")) return "FAQ";
            if (s.Contains("disponibilidade")) return "Disponibilidade";
        }
        return null;
    }

    private static bool Is(string[] segments, params string[] prefix) =>
        segments.Length >= prefix.Length && prefix.Select((part, index) => segments[index] == part).All(match => match);
}
