using System.Globalization;
using System.Text.RegularExpressions;
using APIBack.GestaoLogica.Documentos;

namespace APIBack.GestaoLogica.Vinculos;

/// <summary>Regras puras de unidade: unidade do cupom → unidade de estoque, com o fator lido da descrição.</summary>
public static partial class UnidadeRegras
{
    /// <summary>Medida do conteúdo lida na descrição: "C/6" (por embalagem), "2KG", "900ML", "6X1L".</summary>
    public sealed record MedidaLida(int? PorEmbalagem, decimal? Conteudo, string? UnidadeConteudo);

    /// <summary>KG, G, L, ML, UN e "emb" (embalagem sem conteúdo conhecido: PCT, CX, FD...). Desconhecida = null.</summary>
    public static string? Normalizar(string? unidade) => DocumentoRegras.NomeChave(unidade).Replace(" ", "") switch
    {
        "KG" or "KGS" or "KILO" or "QUILO" => "kg",
        "G" or "GR" or "GRS" or "GRAMA" or "GRAMAS" => "g",
        "L" or "LT" or "LTS" or "LITRO" or "LITROS" => "l",
        "ML" => "ml",
        "UN" or "UND" or "UNID" or "UNI" or "UNIDADE" or "PC" or "PCA" or "PECA" => "un",
        "DZ" or "DUZIA" => "dz",
        "PCT" or "PCTE" or "PAC" or "PACOTE" or "CX" or "CXA" or "CAIXA" or "FD" or "FDO" or "FARDO" or "SC" or "SACO"
            or "BD" or "BDJ" or "BANDEJA" or "EMB" or "KIT" or "GF" or "GARRAFA" or "LATA" => "emb",
        _ => null,
    };

    public static MedidaLida LerDescricao(string? descricao)
    {
        // Texto cru em maiúsculas: NomeChave apagaria a barra de "C/6" e a vírgula de "1,5L".
        var cru = (descricao ?? "").ToUpperInvariant();
        int? por = null;
        if (PorEmbalagemRegex().Match(cru) is { Success: true } m) por = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        else if (UnidadesRegex().Match(cru) is { Success: true } u) por = int.Parse(u.Groups[1].Value, CultureInfo.InvariantCulture);

        if (MultiplaRegex().Match(cru) is { Success: true } x)
        {
            var (valor, un) = Base(Numero(x.Groups[2].Value), x.Groups[3].Value);
            return new MedidaLida(int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture), valor, un);
        }
        if (MedidaRegex().Matches(cru).LastOrDefault() is { } medida)
        {
            var (valor, un) = Base(Numero(medida.Groups[1].Value), medida.Groups[2].Value);
            return new MedidaLida(por, valor, un);
        }
        return new MedidaLida(por, null, null);
    }

    /// <summary>
    /// Converte a linha para a unidade de estoque. <paramref name="unidadeAlvo"/> é a do insumo vinculado (null = ainda sem
    /// vínculo); <paramref name="fatorAprendido"/> é o que a loja confirmou antes para esse vínculo. Sem pista suficiente
    /// devolve null e a tela pergunta (<see cref="Pergunta"/>).
    /// </summary>
    public static Conversao? Converter(ItemLido item, string? unidadeAlvo, decimal? fatorAprendido)
    {
        var qtd = item.Quantidade;
        var un = item.Unidade.ToUpperInvariant();
        if (fatorAprendido is > 0 && UnidadeBase.Valida(unidadeAlvo))
            return Nova(qtd * fatorAprendido.Value, unidadeAlvo!, fatorAprendido.Value, "vinculo",
                $"{Q(qtd)} {un} × {Q(fatorAprendido.Value)} {unidadeAlvo} (fator já confirmado pela loja)");

        var unidade = Normalizar(item.Unidade);
        var medida = LerDescricao(item.Descricao);
        Conversao? r = unidade switch
        {
            "kg" => Nova(qtd * 1000, UnidadeBase.G, 1000, "unidade", $"{Q(qtd)} KG = {Q(qtd * 1000)} g"),
            "g" => Nova(qtd, UnidadeBase.G, 1, "unidade", $"{Q(qtd)} g"),
            "l" => Nova(qtd * 1000, UnidadeBase.Ml, 1000, "unidade", $"{Q(qtd)} L = {Q(qtd * 1000)} ml"),
            "ml" => Nova(qtd, UnidadeBase.Ml, 1, "unidade", $"{Q(qtd)} ml"),
            "dz" => Nova(qtd * 12, UnidadeBase.Un, 12, "unidade", $"{Q(qtd)} DZ × 12 = {Q(qtd * 12)} un"),
            "un" or "emb" => PorDescricao(qtd, un, unidade, medida, unidadeAlvo),
            _ => null,
        };
        // Conversão que não chega na unidade do insumo vinculado não serve (ex.: KG para um insumo em un).
        return r is not null && unidadeAlvo is not null && r.Unidade != unidadeAlvo ? null : r;
    }

    /// <summary>Pergunta para a pessoa quando não deu para converter sozinho.</summary>
    public static string Pergunta(ItemLido item, string? unidadeAlvo) => unidadeAlvo is null
        ? $"Quanto vem em 1 {item.Unidade.ToUpperInvariant()} de \"{item.Descricao}\"?"
        : $"Quantos {unidadeAlvo} vêm em 1 {item.Unidade.ToUpperInvariant()} de \"{item.Descricao}\"?";

    private static Conversao? PorDescricao(decimal qtd, string un, string unidade, MedidaLida m, string? alvo)
    {
        var porUn = alvo is null or UnidadeBase.Un;
        var porMedida = alvo is null or UnidadeBase.G or UnidadeBase.Ml;
        // Sem alvo, contagem ("C/6") vence medida: o pão C/6 é estocado em unidades.
        if (porUn && m.PorEmbalagem is { } n and > 0)
            return Nova(qtd * n, UnidadeBase.Un, n, "descricao", $"{Q(qtd)} {un} × {n} un (\"C/{n}\" na descrição) = {Q(qtd * n)} un");
        if (porMedida && m.Conteudo is { } c && m.UnidadeConteudo is { } uc && (alvo is null || alvo == uc))
        {
            var fator = c * (m.PorEmbalagem ?? 1);
            return Nova(qtd * fator, uc, fator, "descricao", $"{Q(qtd)} {un} × {Q(fator)} {uc} (medida na descrição) = {Q(qtd * fator)} {uc}");
        }
        if (unidade == "un" && alvo is null or UnidadeBase.Un)
            return Nova(qtd, UnidadeBase.Un, 1, "unidade", $"{Q(qtd)} un");
        return null;
    }

    private static Conversao Nova(decimal qtd, string un, decimal fator, string origem, string explicacao) =>
        new(Math.Round(qtd, 4), un, fator, origem, explicacao);

    private static (decimal, string) Base(decimal valor, string unidade) => unidade switch
    {
        "KG" => (valor * 1000, UnidadeBase.G),
        "G" or "GR" => (valor, UnidadeBase.G),
        "L" or "LT" => (valor * 1000, UnidadeBase.Ml),
        _ => (valor, UnidadeBase.Ml),
    };

    private static decimal Numero(string s) => decimal.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);

    private static string Q(decimal v) => v.ToString("0.####", CultureInfo.GetCultureInfo("pt-BR"));

    [GeneratedRegex(@"\bC\s*/\s*(\d{1,4})\b")]
    private static partial Regex PorEmbalagemRegex();

    [GeneratedRegex(@"\b(\d{1,4})\s*(?:UN|UND|UNID|UNIDADES)\b")]
    private static partial Regex UnidadesRegex();

    [GeneratedRegex(@"\b(\d{1,3})\s*X\s*(\d+(?:[.,]\d+)?)\s*(KG|GR|G|ML|LT|L)\b")]
    private static partial Regex MultiplaRegex();

    [GeneratedRegex(@"(?<![\dX])(\d+(?:[.,]\d+)?)\s*(KG|GR|G|ML|LT|L)\b")]
    private static partial Regex MedidaRegex();
}
