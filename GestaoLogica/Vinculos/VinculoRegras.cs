using System.Globalization;
using APIBack.GestaoLogica.Documentos;

namespace APIBack.GestaoLogica.Vinculos;

/// <summary>Regras puras de vínculo: divergência de preço, semelhança de nomes e ordem das sugestões.</summary>
public static class VinculoRegras
{
    /// <summary>Preço por unidade base até 2,5× acima (ou abaixo) da mediana é variação normal de preço.</summary>
    public const decimal LimiteDivergencia = 2.5m;

    /// <summary>Abaixo disso a semelhança de nomes não vira sugestão.</summary>
    public const decimal SemelhancaMinima = 0.4m;

    public static decimal PrecoBase(decimal valorTotal, decimal quantidadeBase) =>
        quantidadeBase <= 0 ? 0 : Math.Round(valorTotal / quantidadeBase, 6);

    public static decimal Mediana(IReadOnlyList<decimal> valores)
    {
        if (valores.Count == 0) return 0;
        var o = valores.Order().ToArray();
        return o.Length % 2 == 1 ? o[o.Length / 2] : (o[o.Length / 2 - 1] + o[o.Length / 2]) / 2;
    }

    /// <summary>
    /// Compara o preço por unidade base da linha com a mediana do histórico do vínculo. Muito acima: a quantidade
    /// provavelmente está em embalagens (sacola 1 UN que é um pacote de 50); muito abaixo: já estava em unidades base.
    /// </summary>
    public static DivergenciaPreco? Divergencia(decimal precoBase, IReadOnlyList<decimal> historico, decimal fatorUsado, string unidade)
    {
        var validos = historico.Where(h => h > 0).ToList();
        if (precoBase <= 0 || validos.Count == 0) return null;
        var mediana = Mediana(validos);
        var razao = Math.Round(precoBase / mediana, 4);
        if (razao >= LimiteDivergencia)
        {
            var vezes = Math.Round(razao, 0);
            return new DivergenciaPreco(precoBase, mediana, razao, fatorUsado * vezes,
                $"O preço por {unidade} ({Moeda(precoBase)}) está {vezes:0}× acima do de costume ({Moeda(mediana)}). " +
                $"Cada unidade do cupom deve ter {Q(fatorUsado * vezes)} {unidade}: confira a quantidade.");
        }
        if (razao <= 1 / LimiteDivergencia)
        {
            var vezes = Math.Round(1 / razao, 0);
            return new DivergenciaPreco(precoBase, mediana, razao, Math.Max(1, Math.Round(fatorUsado / vezes, 0)),
                $"O preço por {unidade} ({Moeda(precoBase)}) está {vezes:0}× abaixo do de costume ({Moeda(mediana)}). " +
                "A quantidade pode já estar na unidade de estoque: confira.");
        }
        return null;
    }

    /// <summary>Tokens comparáveis: sem medidas ("2KG", "C/6", "900ML"), sem números soltos e sem letras isoladas.</summary>
    public static IReadOnlyList<string> Tokens(string? texto) => DocumentoRegras.NomeChave(texto)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(t => t.Length > 1 && !t.All(char.IsDigit) && !EhMedida(t))
        .Distinct().ToList();

    /// <summary>
    /// Semelhança de 0 a 1 entre dois nomes. Abreviação do cupom casa com a palavra inteira pelo prefixo
    /// ("MUSS" ↔ "MUSSARELA", "HAMB" ↔ "HAMBURGUER"), desde que tenha 3 letras ou mais.
    /// </summary>
    public static decimal Semelhanca(string? a, string? b)
    {
        var ta = Tokens(a);
        var tb = Tokens(b);
        if (ta.Count == 0 || tb.Count == 0) return 0;
        var usados = new HashSet<int>();
        var casados = 0;
        foreach (var x in ta)
        {
            for (var j = 0; j < tb.Count; j++)
            {
                if (usados.Contains(j) || !Casa(x, tb[j])) continue;
                usados.Add(j);
                casados++;
                break;
            }
        }
        return Math.Round((decimal)casados / (ta.Count + tb.Count - casados), 4);
    }

    /// <summary>Confiança da comunidade: cresce com o número de lojas que confirmaram, sem passar de 0,8 (não é curado).</summary>
    public static decimal ConfiancaComunidade(int lojas) => lojas <= 0 ? 0 : Math.Min(0.8m, 0.6m + 0.04m * lojas);

    /// <summary>Ordena pela confiança (empate: loja → global → comunidade → similar), um por alvo, e termina em "criar novo".</summary>
    public static IReadOnlyList<Sugestao> Ordenar(IEnumerable<Sugestao> candidatas, string descricao, int maximo = 5)
    {
        var lista = candidatas
            .Where(s => s.Origem != OrigemSugestao.CriarNovo)
            .OrderByDescending(s => s.Confianca).ThenBy(s => OrigemSugestao.Ordem(s.Origem))
            .DistinctBy(s => s.InsumoId is { } i ? "i" + i : s.GlobalId is { } g ? "g" + g : "n" + s.Nome)
            .Take(maximo)
            .ToList();
        lista.Add(new Sugestao(OrigemSugestao.CriarNovo, null, null, NomeSugerido(descricao), null, null, 0,
            "Nenhum item conhecido: cadastrar um novo insumo da loja."));
        return lista;
    }

    /// <summary>Nome para "criar novo": a descrição sem medidas e sem códigos, com a primeira letra de cada palavra maiúscula.</summary>
    public static string NomeSugerido(string? descricao)
    {
        var nome = string.Join(' ', Tokens(descricao));
        return nome.Length == 0 ? (descricao ?? "").Trim()
            : CultureInfo.GetCultureInfo("pt-BR").TextInfo.ToTitleCase(nome.ToLowerInvariant());
    }

    private static bool Casa(string a, string b) =>
        a == b || (Math.Min(a.Length, b.Length) >= 3 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)));

    private static bool EhMedida(string t)
    {
        var i = 0;
        while (i < t.Length && char.IsDigit(t[i])) i++;
        return i > 0 && t[i..] is "KG" or "G" or "GR" or "ML" or "L" or "LT" or "UN" or "X";
    }

    private static string Moeda(decimal v) => v.ToString("C4", CultureInfo.GetCultureInfo("pt-BR"));

    private static string Q(decimal v) => v.ToString("0.####", CultureInfo.GetCultureInfo("pt-BR"));
}
