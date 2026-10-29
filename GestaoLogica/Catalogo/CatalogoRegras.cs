using System.Globalization;
using System.Text.RegularExpressions;
using APIBack.GestaoLogica.Documentos;
using APIBack.GestaoLogica.Vinculos;

namespace APIBack.GestaoLogica.Catalogo;

/// <summary>Regras puras do catálogo (sem banco): finalidades, unidades, conversão do cupom e montagem do de-para de compras.</summary>
public static partial class CatalogoRegras
{
    public const int MaxFinalidades = 8, MaxTamanhoFinalidade = 40;

    /// <summary>Finalidades da loja sem vazias e sem repetidas (sem acento/caixa), na ordem em que vieram.</summary>
    public static IReadOnlyList<string> NormalizarFinalidades(IEnumerable<string?>? valores)
    {
        var vistos = new HashSet<string>();
        var saida = new List<string>();
        foreach (var v in valores ?? [])
        {
            var limpo = Regex.Replace(v ?? "", @"\s+", " ").Trim();
            if (limpo.Length == 0 || !vistos.Add(DocumentoRegras.NomeChave(limpo))) continue;
            saida.Add(limpo.Length > MaxTamanhoFinalidade ? limpo[..MaxTamanhoFinalidade] : limpo);
            if (saida.Count == MaxFinalidades) break;
        }
        return saida;
    }

    /// <summary>Unidade da tela (un, kg, g, L, ml) → unidade de estoque do insumo (g, ml, un). Null = inválida.</summary>
    public static string? UnidadeBaseDe(string? unidade) => (unidade ?? "").Trim().ToLowerInvariant() switch
    {
        "un" or "und" or "unidade" => UnidadeBase.Un,
        "g" or "kg" => UnidadeBase.G,
        "ml" or "l" => UnidadeBase.Ml,
        _ => null,
    };

    /// <summary>
    /// Fator (unidades base por 1 unidade do cupom) a partir do texto da tela: "1 PC = 2 kg" → 2000 (g), "1 CX = 12 un" → 12,
    /// "500 ml" → 500. A unidade do lado direito precisa combinar com a do insumo. Null = não deu para entender.
    /// </summary>
    public static decimal? FatorDaConversao(string? texto, string unidadeBase)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        var partes = texto.Split('=', 2);
        var cupom = partes.Length == 2 ? Quantidade().Match(partes[0]) : null;
        var m = Quantidade().Match(partes[^1]);
        if (!m.Success) return null;
        var valor = Numero(m.Groups["n"].Value);
        var porCupom = cupom is { Success: true } ? Numero(cupom.Groups["n"].Value) : 1m;
        if (valor is not > 0 || porCupom is not > 0) return null;
        var multiplicador = (m.Groups["u"].Value.ToLowerInvariant(), unidadeBase) switch
        {
            ("kg", UnidadeBase.G) or ("l", UnidadeBase.Ml) => 1000m,
            ("g", UnidadeBase.G) or ("ml", UnidadeBase.Ml) or ("un" or "und" or "", UnidadeBase.Un) => 1m,
            _ => (decimal?)null,
        };
        return multiplicador is { } x ? Math.Round(valor.Value * x / porCupom.Value, 6) : null;
    }

    /// <summary>Texto da conversão para a tela: 1 PC × 2000 g → "1 PC = 2 kg"; 1 CX × 12 un → "1 CX = 12 un".</summary>
    public static string ConversaoTexto(string unidadeCupom, decimal fator, string unidadeBase)
    {
        var (valor, unidade) = unidadeBase switch
        {
            UnidadeBase.G when fator >= 1000 => (fator / 1000, "kg"),
            UnidadeBase.Ml when fator >= 1000 => (fator / 1000, "L"),
            _ => (fator, unidadeBase),
        };
        return $"1 {unidadeCupom} = {valor.ToString("0.###", CultureInfo.GetCultureInfo("pt-BR"))} {unidade}";
    }

    public static string IdDeNome(string fornecedorCnpj, string nomeChave) => $"nome:{fornecedorCnpj}:{nomeChave}";

    /// <summary>Uma linha de documento que virou compra (ou seja, um uso do nome no cupom).</summary>
    public sealed record LinhaLida(Guid DocumentoId, int Linha, DateTimeOffset Quando, string FornecedorCnpj, string? FornecedorNome,
        string Descricao, string NomeChave);

    public sealed record VinculoLido(Guid Id, string FornecedorCnpj, string NomeChave, string Descricao, string UnidadeCupom,
        Guid InsumoId, string? InsumoNome, Guid? GlobalId, string? StatusGlobal, string? OrigemLoja, string UnidadeBase,
        decimal Fator, DateTimeOffset ConfirmadoEm, string? ConfirmadoPorNome);

    public sealed record NaoControlarLido(string FornecedorCnpj, string NomeChave, string Descricao, DateTimeOffset Em, string? PorNome);

    /// <summary>
    /// Monta o de-para de compras: cada vínculo confirmado, cada nome lido sem vínculo e cada "não controlar". Um vínculo com
    /// fornecedor '' (qualquer um) cobre o nome de todos os fornecedores. Ordem: o que precisa de atenção primeiro, depois o mais usado.
    /// </summary>
    public static IReadOnlyList<DeParaCompraDto> MontarDePara(IReadOnlyList<VinculoLido> vinculos, IReadOnlyList<LinhaLida> linhas,
        IReadOnlyList<NaoControlarLido> naoControlar, IReadOnlyDictionary<string, IReadOnlyList<EventoCatalogoDto>>? eventosPorNome = null)
    {
        static string Mes(DateTimeOffset d) => d.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        IReadOnlyList<EventoCatalogoDto> Eventos(string chave) =>
            eventosPorNome is not null && eventosPorNome.TryGetValue(chave, out var e) ? e : [];

        var saida = new List<DeParaCompraDto>();
        var cobertas = new HashSet<(string, string)>();
        var semVinculo = linhas.ToList();

        foreach (var v in vinculos)
        {
            var usos = linhas.Where(l => l.NomeChave == v.NomeChave && (v.FornecedorCnpj == "" || l.FornecedorCnpj == v.FornecedorCnpj)).ToList();
            semVinculo.RemoveAll(usos.Contains);
            cobertas.Add((v.FornecedorCnpj, v.NomeChave));
            var ultima = usos.MaxBy(u => u.Quando);
            var origem = v.OrigemLoja == OrigemCatalogo.Novo && v.StatusGlobal != "validado" ? OrigemCatalogo.Novo
                : v.GlobalId is not null && v.StatusGlobal is "validado" or "mesclado" ? OrigemCatalogo.Global : OrigemCatalogo.Loja;
            // Item novo já nasce candidato da curadoria (em análise); "sugerir ao Zippy" cria o candidato quando não há.
            var curadoria = CuradoriaLoja.De(v.StatusGlobal);
            var historico = new List<EventoCatalogoDto>(Eventos(v.NomeChave))
            {
                new(v.Id, v.ConfirmadoEm, "Vínculo confirmado", $"{v.Descricao} → {v.InsumoNome ?? "insumo"} · vale agora para a sua loja", v.ConfirmadoPorNome),
            };
            saida.Add(new DeParaCompraDto(v.Id.ToString(), v.Id, v.Descricao, v.NomeChave, v.FornecedorCnpj,
                ultima?.FornecedorNome, v.InsumoId, v.InsumoNome, origem, curadoria, v.GlobalId,
                usos.GroupBy(u => Mes(u.Quando)).ToDictionary(g => g.Key, g => g.Count()), usos.Count, ultima?.DocumentoId, ultima?.Linha,
                ultima?.Quando, ConversaoTexto(v.UnidadeCupom, v.Fator, v.UnidadeBase), false,
                historico.OrderByDescending(h => h.Quando).ToList()));
        }

        foreach (var g in semVinculo.GroupBy(l => (l.FornecedorCnpj, l.NomeChave)))
        {
            var marcado = naoControlar.FirstOrDefault(n => n.NomeChave == g.Key.NomeChave && (n.FornecedorCnpj == "" || n.FornecedorCnpj == g.Key.FornecedorCnpj));
            var ultima = g.MaxBy(u => u.Quando)!;
            var historico = new List<EventoCatalogoDto>(Eventos(g.Key.NomeChave))
            {
                new(ultima.DocumentoId, g.Min(u => u.Quando), "Lido pela primeira vez", ultima.FornecedorNome, null),
            };
            saida.Add(new DeParaCompraDto(IdDeNome(g.Key.FornecedorCnpj, g.Key.NomeChave), null, ultima.Descricao, g.Key.NomeChave,
                g.Key.FornecedorCnpj, ultima.FornecedorNome, null, null, OrigemCatalogo.Desconhecido, CuradoriaLoja.NaoEnviado, null,
                g.GroupBy(u => Mes(u.Quando)).ToDictionary(x => x.Key, x => x.Count()), g.Count(), ultima.DocumentoId, ultima.Linha,
                ultima.Quando, null, marcado is not null, historico.OrderByDescending(h => h.Quando).ToList()));
            if (marcado is not null) cobertas.Add((marcado.FornecedorCnpj, marcado.NomeChave));
        }

        // "Não controlar" de um nome que ainda não apareceu em compra registrada (ou só em compras antigas).
        foreach (var n in naoControlar.Where(n => !cobertas.Contains((n.FornecedorCnpj, n.NomeChave))
                     && !saida.Any(s => s.NomeChave == n.NomeChave && (n.FornecedorCnpj == "" || s.FornecedorCnpj == n.FornecedorCnpj))))
            saida.Add(new DeParaCompraDto(IdDeNome(n.FornecedorCnpj, n.NomeChave), null, n.Descricao, n.NomeChave, n.FornecedorCnpj, null,
                null, null, OrigemCatalogo.Desconhecido, CuradoriaLoja.NaoEnviado, null, new Dictionary<string, int>(), 0, null, null, null,
                null, true, Eventos(n.NomeChave)));

        return saida
            .OrderBy(d => d.InsumoId is null && !d.NaoControlar ? 0 : 1)
            .ThenByDescending(d => d.UsosTotal)
            .ThenBy(d => d.NomeNoCupom, StringComparer.Create(CultureInfo.GetCultureInfo("pt-BR"), true))
            .ToList();
    }

    /// <summary>Ordena a busca da conferência: começa com o texto vem antes; depois a semelhança de nome.</summary>
    public static decimal Pontuar(string texto, string nome)
    {
        var termo = DocumentoRegras.NomeChave(texto);
        var chave = DocumentoRegras.NomeChave(nome);
        if (termo.Length == 0) return 0;
        if (chave == termo) return 1;
        if (chave.StartsWith(termo, StringComparison.Ordinal)) return 0.95m;
        if (chave.Contains(termo, StringComparison.Ordinal)) return 0.85m;
        return VinculoRegras.Semelhanca(texto, nome);
    }

    private static decimal? Numero(string s) =>
        decimal.TryParse(s.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : null;

    [GeneratedRegex(@"(?<n>\d+(?:[.,]\d+)?)\s*(?<u>[a-zA-Z]*)")]
    private static partial Regex Quantidade();
}
