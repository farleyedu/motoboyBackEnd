using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Estoque;
using APIBack.Integrations.Ifood.Orders;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Vendas;

/// <summary>
/// Itens vendidos por pedido, iguais para todos os canais, sempre com o produto real do cardápio (ou nulo = "sem produto"):
/// pedido do cardápio lê pedido_item; pedido iFood lê os itens do snapshot oficial (nunca alterado) e resolve pelo vínculo confirmado.
/// Linha avulsa do cardápio resolve pelo de-para por nome. Produto de outra loja ou inexistente vira nulo; produto excluído continua valendo.
/// </summary>
public interface IItensVendidosLeitor
{
    Task<IReadOnlyList<ItemVendido>> Ler(Guid loja, IReadOnlyCollection<int> pedidoIds, CancellationToken ct);
}

public sealed class ItensVendidosLeitor(NpgsqlDataSource dataSource) : IItensVendidosLeitor
{
    private sealed class Mapa { public string NomeChave { get; set; } = ""; public Guid ProdutoId { get; set; } }

    private sealed class Linha
    {
        public int PedidoId { get; set; }
        public Guid? ProdutoId { get; set; }
        public string Nome { get; set; } = "";
        public int Quantidade { get; set; }
    }

    public async Task<IReadOnlyList<ItemVendido>> Ler(Guid loja, IReadOnlyCollection<int> pedidoIds, CancellationToken ct)
    {
        if (pedidoIds.Count == 0) return Array.Empty<ItemVendido>();
        var ids = pedidoIds.Distinct().ToArray();
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var daLoja = (await c.QueryAsync<int>(new CommandDefinition(
            "SELECT id FROM pedido WHERE id_estabelecimento = @loja AND id = ANY(@ids)", new { loja, ids }, cancellationToken: ct))).ToHashSet();
        var lidos = daLoja.ToArray();
        if (lidos.Length == 0) return Array.Empty<ItemVendido>();

        var itens = (await c.QueryAsync<Linha>(new CommandDefinition("""
SELECT pi.pedido_id AS PedidoId,
       (SELECT cp.id FROM cardapio_produto cp WHERE cp.id = pi.produto_id AND cp.id_estabelecimento = @loja) AS ProdutoId,
       pi.nome AS Nome, pi.quantidade AS Quantidade
  FROM pedido_item pi WHERE pi.pedido_id = ANY(@lidos) ORDER BY pi.pedido_id, pi.ordem, pi.id
""", new { loja, lidos }, cancellationToken: ct))).ToList();

        var semItens = lidos.Except(itens.Select(i => i.PedidoId)).ToArray();
        if (semItens.Length > 0 && await TemIfood(c, ct))
            itens.AddRange(await c.QueryAsync<Linha>(new CommandDefinition("""
SELECT l.pedido_id AS PedidoId,
       (SELECT v.produto_id FROM ifood_produto_vinculo v
          JOIN cardapio_produto cp ON cp.id = v.produto_id AND cp.id_estabelecimento = v.estabelecimento_id AND cp.deleted_at IS NULL
         WHERE v.estabelecimento_id = l.estabelecimento_id AND v.merchant_id = l.merchant_id AND v.tipo = 'item' AND v.estado = 'confirmado'
           AND v.chave_id = t.item->>'id') AS ProdutoId,
       coalesce(nullif(btrim(t.item->>'name'), ''), '(item sem nome)') AS Nome,
       CASE WHEN t.item->>'quantity' ~ '^[0-9]+$' THEN (t.item->>'quantity')::int ELSE 0 END AS Quantidade
  FROM ifood_order_links l
  JOIN ifood_order_snapshots s ON s.merchant_id = l.merchant_id AND s.external_order_id = l.external_order_id AND s.estabelecimento_id = l.estabelecimento_id
  CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(s.payload->'items') = 'array' THEN s.payload->'items' ELSE '[]'::jsonb END)
       WITH ORDINALITY AS t(item, n)
 WHERE l.estabelecimento_id = @loja AND l.pedido_id = ANY(@semItens)
 ORDER BY l.pedido_id, t.n
""", new { loja, semItens }, cancellationToken: ct)));

        await ResolverPorNome(c, loja, itens, ct);
        return itens.Where(i => i.Quantidade > 0).OrderBy(i => i.PedidoId).Select(i => new ItemVendido(i.PedidoId, i.ProdutoId, i.Nome, i.Quantidade)).ToList();
    }

    /// <summary>Linha avulsa (sem produto) do cardápio: o de-para por nome, feito na Gestão, decide o produto.</summary>
    private static async Task ResolverPorNome(NpgsqlConnection c, Guid loja, List<Linha> itens, CancellationToken ct)
    {
        if (!itens.Any(i => i.ProdutoId == null)) return;
        if (!await c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT to_regclass('gestao_venda_vinculo') IS NOT NULL", cancellationToken: ct))) return;
        var mapa = (await c.QueryAsync<Mapa>(new CommandDefinition("""
SELECT v.nome_chave AS NomeChave, v.produto_id AS ProdutoId FROM gestao_venda_vinculo v
  JOIN cardapio_produto cp ON cp.id = v.produto_id AND cp.id_estabelecimento = v.estabelecimento_id AND cp.deleted_at IS NULL
 WHERE v.estabelecimento_id = @loja AND v.canal = 'cardapio'
""", new { loja }, cancellationToken: ct))).ToDictionary(x => x.NomeChave, x => x.ProdutoId);
        if (mapa.Count == 0) return;
        foreach (var i in itens.Where(i => i.ProdutoId == null))
            if (mapa.TryGetValue(IfoodItemLinkRules.NormalizeName(i.Nome), out var produto)) i.ProdutoId = produto;
    }

    private static Task<bool> TemIfood(NpgsqlConnection c, CancellationToken ct) => c.ExecuteScalarAsync<bool>(new CommandDefinition(
        "SELECT to_regclass('ifood_order_links') IS NOT NULL AND to_regclass('ifood_order_snapshots') IS NOT NULL AND to_regclass('ifood_produto_vinculo') IS NOT NULL",
        cancellationToken: ct));
}
