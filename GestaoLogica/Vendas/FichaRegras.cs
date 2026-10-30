using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Estoque;

namespace APIBack.GestaoLogica.Vendas;

/// <summary>Regras puras da ficha técnica: quem é embalagem, quais modalidades valem e o que não pode ser gravado.</summary>
public static class FichaRegras
{
    /// <summary>Decisão provisória: embalagem é o insumo cuja categoria é de embalagem ou descartável (a ficha não tem outro marcador).</summary>
    public static bool Embalagem(string? categoria)
    {
        var c = (categoria ?? "").ToLowerInvariant();
        return c.Contains("embalag") || c.Contains("descart");
    }

    /// <summary>Nulo = todas as modalidades (lista vazia, ou as três juntas). Lista inválida é erro, nunca é descartada em silêncio.</summary>
    public static IReadOnlyList<string>? Modalidades(IReadOnlyList<string>? pedidas)
    {
        if (pedidas == null || pedidas.Count == 0) return null;
        var lista = pedidas.Select(m => (m ?? "").Trim().ToLowerInvariant()).Distinct().ToList();
        var invalida = lista.FirstOrDefault(m => !Modalidade.Todas.Contains(m));
        if (invalida != null) throw Invalido($"Modalidade desconhecida: {(invalida.Length == 0 ? "(vazia)" : invalida)}.");
        if (lista.Count == Modalidade.Todas.Length) return null;
        return Modalidade.Todas.Where(lista.Contains).ToList();
    }

    public static IReadOnlyList<string> Alcance(IReadOnlyList<string>? modalidades) => modalidades ?? Modalidade.Todas;

    /// <summary>
    /// Valida e normaliza a ficha inteira antes de gravar. A unidade é a do insumo (sem conversão), o insumo é da loja e ativo,
    /// e um mesmo insumo não pode valer duas vezes na mesma modalidade (a baixa descontaria em dobro).
    /// </summary>
    public static IReadOnlyList<ComponenteFicha> Validar(IReadOnlyList<ComponenteRequest>? pedidos, IReadOnlyDictionary<Guid, InsumoFicha> insumos)
    {
        var saida = new List<ComponenteFicha>();
        foreach (var p in pedidos ?? Array.Empty<ComponenteRequest>())
        {
            if (!insumos.TryGetValue(p.InsumoId, out var insumo)) throw Invalido("Ingrediente não encontrado nesta loja.");
            if (!insumo.Ativo) throw Invalido($"O ingrediente \"{insumo.Nome}\" está inativo.");
            var quantidade = Math.Round(p.Quantidade, 4);
            if (quantidade <= 0) throw Invalido($"A quantidade de \"{insumo.Nome}\" precisa ser maior que zero.");
            if (quantidade > 99_999_999m) throw Invalido($"A quantidade de \"{insumo.Nome}\" é grande demais.");
            if (!string.IsNullOrWhiteSpace(p.Unidade) && !string.Equals(p.Unidade.Trim(), insumo.Unidade, StringComparison.OrdinalIgnoreCase))
                throw Invalido($"\"{insumo.Nome}\" é medido em {insumo.Unidade}; informe a quantidade nessa unidade.");
            saida.Add(new ComponenteFicha(p.InsumoId, insumo.Nome, quantidade, insumo.Unidade, Modalidades(p.Modalidades)));
        }
        foreach (var grupo in saida.GroupBy(c => c.InsumoId))
        {
            var vistas = new HashSet<string>();
            foreach (var c in grupo)
                foreach (var m in Alcance(c.Modalidades))
                    if (!vistas.Add(m)) throw Invalido($"\"{c.Nome}\" aparece duas vezes na ficha para {m}.");
        }
        return saida;
    }

    private static GestaoException Invalido(string mensagem) => new("CAMPO_INVALIDO", mensagem, 422);
}
