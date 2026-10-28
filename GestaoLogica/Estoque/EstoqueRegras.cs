using APIBack.Acontecimentos;

namespace APIBack.GestaoLogica.Estoque;

/// <summary>Regras puras de estoque: divergência de inventário e consumo da baixa por venda.</summary>
public static class EstoqueRegras
{
    /// <summary>Compara a contagem com o saldo do livro. Insumo contado sem movimento nenhum tem esperado zero.</summary>
    public static IReadOnlyList<DivergenciaInventario> Divergencias(IReadOnlyList<Contagem> contagens,
        IReadOnlyDictionary<Guid, SaldoInsumo> saldos)
    {
        if (contagens.Count == 0) throw Invalido("Informe ao menos uma contagem.");
        if (contagens.Any(c => c.Contado < 0)) throw Invalido("Contagem não pode ser negativa.");
        if (contagens.GroupBy(c => c.InsumoId).Any(g => g.Count() > 1)) throw Invalido("O mesmo insumo foi contado duas vezes.");
        return contagens.Select(c =>
        {
            var s = saldos.TryGetValue(c.InsumoId, out var x) ? x : throw Invalido("Insumo não encontrado nesta loja.");
            var contado = Math.Round(c.Contado, 4);
            return new DivergenciaInventario(c.InsumoId, s.Nome, s.Unidade, s.Saldo, contado, Math.Round(contado - s.Saldo, 4));
        }).ToList();
    }

    /// <summary>
    /// Soma o consumo dos pedidos pela ficha de cada produto e pela modalidade (embalagem de delivery só no delivery).
    /// Pedido com item sem ficha (ou avulso, sem produto) fica de fora inteiro: baixar metade dele esconderia o erro.
    /// </summary>
    public static (IReadOnlyList<ConsumoInsumo> Consumo, IReadOnlyList<int> Pedidos, IReadOnlyList<PedidoSemFicha> SemFicha) Consumo(
        IReadOnlyList<PedidoBaixa> pedidos, IReadOnlyList<ItemVendido> itens, IReadOnlyDictionary<Guid, FichaTecnica> fichas)
    {
        var consumo = new Dictionary<Guid, ConsumoInsumo>();
        var baixados = new List<int>();
        var semFicha = new List<PedidoSemFicha>();
        foreach (var p in pedidos)
        {
            var doPedido = itens.Where(i => i.PedidoId == p.PedidoId).ToList();
            var faltam = doPedido.Where(i => i.ProdutoId is not { } id || !fichas.ContainsKey(id)).Select(i => i.Nome).Distinct().ToList();
            if (doPedido.Count == 0 || faltam.Count > 0)
            {
                semFicha.Add(new PedidoSemFicha(p.PedidoId, doPedido.Count == 0 ? new[] { "(pedido sem itens)" } : faltam));
                continue;
            }
            foreach (var item in doPedido)
                foreach (var comp in fichas[item.ProdutoId!.Value].Componentes)
                {
                    if (comp.Modalidades is { Count: > 0 } m && !m.Contains(p.Modalidade)) continue;
                    var qtd = comp.Quantidade * item.Quantidade;
                    consumo[comp.InsumoId] = consumo.TryGetValue(comp.InsumoId, out var atual)
                        ? atual with { Quantidade = atual.Quantidade + qtd }
                        : new ConsumoInsumo(comp.InsumoId, comp.Nome, comp.Unidade, qtd);
                }
            baixados.Add(p.PedidoId);
        }
        return (consumo.Values.Where(c => c.Quantidade != 0).Select(c => c with { Quantidade = Math.Round(c.Quantidade, 4) })
            .OrderBy(c => c.Nome).ToList(), baixados, semFicha);
    }

    public static void ValidarPedidos(IReadOnlyList<PedidoBaixa> pedidos)
    {
        if (pedidos.Count == 0) throw Invalido("Escolha ao menos um pedido.");
        if (pedidos.Any(p => !Modalidade.Todas.Contains(p.Modalidade)))
            throw Invalido("Modalidade deve ser salao, retirada ou delivery.");
        if (pedidos.GroupBy(p => p.PedidoId).Any(g => g.Count() > 1)) throw Invalido("Pedido repetido na lista.");
    }

    private static GestaoException Invalido(string m) => new("CAMPO_INVALIDO", m, 422);
}
