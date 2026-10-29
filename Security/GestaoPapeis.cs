using APIBack.Acontecimentos;

namespace APIBack.Security;

/// <summary>
/// Papeis da gestao (regra 11 do bloco comum). Gerente ve tudo, com historico de todos os meses. Operador de caixa ve
/// so o Caixa do dia e o Estoque e faz o fechamento com contagem as cegas (sem ver o saldo esperado).
/// O papel sai das permissoes da loja (Financeiro, Caixa, Estoque), entao o dono ajusta pela tela de permissoes.
/// </summary>
public static class GestaoPapeis
{
    public const string Gerente = "gerente", OperadorCaixa = "operador_caixa";

    /// <summary>Areas da doca, na ordem do prototipo. Curadoria e da Zippy (super admin), nunca da loja.</summary>
    public static readonly string[] Areas = { "financeiro", "caixa", "compras", "estoque", "catalogo", "equipe", "despesas", "curadoria" };
    private static readonly string[] AreasOperador = { "caixa", "estoque" };

    // Permissoes que este modulo acrescenta ao catalogo (EstablishmentPermissions.Catalog).
    public static readonly string[] AcoesCaixa = { "visualizar", "lancar", "fechar" };
    public static readonly string[] AcoesEstoque = { "visualizar", "contar", "confirmar" };
    public const string AcaoFecharMes = "fechar_mes";

    public sealed record Acesso(string? Papel, IReadOnlyList<string> Areas, bool Historico, bool SaldoEsperadoNoFechamento,
        bool PodeLancar, bool PodeCorrigir, bool PodeFecharMes, bool PodeConferirEstoque);

    public static string? Papel(Func<string, string, bool> tem, bool superAdmin)
    {
        if (superAdmin || tem("Financeiro", "visualizar")) return Gerente;
        if (tem("Caixa", "visualizar")) return OperadorCaixa;
        return null;
    }

    public static Acesso Resolver(Func<string, string, bool> tem, bool superAdmin)
    {
        var papel = Papel(tem, superAdmin);
        return papel switch
        {
            Gerente => new Acesso(papel, Areas.Where(a => superAdmin || a != "curadoria").ToList(), true, true,
                superAdmin || tem("Financeiro", "criar"), superAdmin || tem("Financeiro", "editar") || tem("Financeiro", "cancelar"),
                superAdmin || tem("Financeiro", AcaoFecharMes), true),
            OperadorCaixa => new Acesso(papel, AreasOperador.Where(a => a != "estoque" || tem("Estoque", "visualizar")).ToList(),
                false, false, tem("Caixa", "lancar"), false, false, tem("Estoque", "contar")),
            _ => new Acesso(null, Array.Empty<string>(), false, false, false, false, false, false),
        };
    }

    /// <summary>
    /// Leitura: o gerente le qualquer mes e dia. O operador so le o dia de hoje, nos modulos caixa e estoque.
    /// Devolve o filtro ja restrito; sem permissao, 403.
    /// </summary>
    public static PeriodoFiltro RestringirLeitura(Acesso acesso, PeriodoFiltro filtro, DateOnly hoje)
    {
        if (acesso.Papel == Gerente) return filtro;
        if (acesso.Papel != OperadorCaixa) throw SemPermissao("Você não tem acesso à gestão desta loja.");
        if (filtro.Mes is { } mes && AcontecimentoRegras.Mes(mes) != AcontecimentoRegras.Mes(hoje) || filtro.Dia is { } dia && dia != hoje
            || filtro.Ate is { } ate && ate != hoje)
            throw SemPermissao("O operador de caixa vê só o dia de hoje.");
        var modulo = filtro.Modulo ?? ModuloGestao.Caixa;
        if (modulo is not (ModuloGestao.Caixa or ModuloGestao.Estoque) || !acesso.Areas.Contains(modulo))
            throw SemPermissao("O operador de caixa vê só o Caixa e o Estoque.");
        filtro.Mes = AcontecimentoRegras.Mes(hoje);
        filtro.Dia = hoje;
        filtro.Ate = null;
        filtro.Modulo = modulo;
        return filtro;
    }

    /// <summary>
    /// Publicacao pelo Painel. Gerente publica com Financeiro:criar. Operador so registra no caixa (Caixa:lancar) ou
    /// contagem/conferencia de estoque (Estoque:contar), sempre com data de hoje.
    /// </summary>
    public static void ValidarPublicacao(Acesso acesso, PublicarAcontecimentoRequest request, DateOnly diaDoFato, DateOnly hoje)
    {
        if (acesso.Papel == Gerente)
        {
            if (!acesso.PodeLancar) throw SemPermissao("Você não pode registrar lançamentos.");
            return;
        }
        if (acesso.Papel != OperadorCaixa) throw SemPermissao("Você não tem acesso à gestão desta loja.");
        if (diaDoFato != hoje) throw SemPermissao("O operador de caixa registra só o dia de hoje.");
        var ok = request.Modulo switch
        {
            ModuloGestao.Caixa => acesso.PodeLancar && (request.Movimentos?.Count ?? 0) == 0,
            ModuloGestao.Estoque => acesso.PodeConferirEstoque && (request.Lancamentos?.Count ?? 0) == 0,
            _ => false,
        };
        if (!ok) throw SemPermissao("O operador de caixa registra só no Caixa e no Estoque.");
    }

    public static void ExigirCorrecao(Acesso acesso)
    {
        if (!acesso.PodeCorrigir) throw SemPermissao("Só o gerente faz estorno e ajuste.");
    }

    public static void ExigirFecharMes(Acesso acesso)
    {
        if (!acesso.PodeFecharMes) throw SemPermissao("Só o gerente fecha o mês.");
    }

    /// <summary>Permissoes sugeridas ao criar a pessoa com esse papel (a tela de permissoes continua mandando).</summary>
    public static Dictionary<string, string[]> PermissoesPadrao(string papel) => papel switch
    {
        OperadorCaixa => new(StringComparer.OrdinalIgnoreCase)
        {
            ["Caixa"] = AcoesCaixa,
            ["Estoque"] = new[] { "visualizar", "contar" },
        },
        Gerente => new(StringComparer.OrdinalIgnoreCase)
        {
            ["Financeiro"] = new[] { "visualizar", "criar", "editar", "cancelar", "exportar", "configurar", AcaoFecharMes },
            ["Caixa"] = AcoesCaixa,
            ["Estoque"] = AcoesEstoque,
        },
        _ => new(StringComparer.OrdinalIgnoreCase),
    };

    private static GestaoException SemPermissao(string mensagem) => new("SEM_PERMISSAO", mensagem, 403);
}
