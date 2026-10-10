using APIBack.DTOs.Cardapio;
using APIBack.DTOs.Delivery;
using Dapper;
using Npgsql;

namespace APIBack.Repository;

public sealed class CardapioMotoboyAttentionRepository(NpgsqlDataSource source)
{
    public async Task<CardapioMotoboyAttentionDto> ReadAsync(Guid store, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        using var rows = await connection.QueryMultipleAsync(new CommandDefinition("""
SELECT id FROM cardapio_produto WHERE id_estabelecimento=@Store AND deleted_at IS NULL AND atencao_motoboy ORDER BY id;
SELECT id FROM cardapio_grupo_adicional WHERE id_estabelecimento=@Store AND deleted_at IS NULL AND atencao_motoboy ORDER BY id;
""", new { Store = store }, cancellationToken: ct));
        return new() { Produtos = (await rows.ReadAsync<Guid>()).ToList(), Adicionais = (await rows.ReadAsync<Guid>()).ToList() };
    }

    public async Task<bool> SetAsync(Guid store, string kind, Guid id, bool attention, CancellationToken ct)
    {
        // Nomes de tabelas vêm exclusivamente desta lista, nunca do corpo da requisição.
        var table = kind switch { "produtos" => "cardapio_produto", "adicionais" => "cardapio_grupo_adicional", _ => throw new ArgumentException("Tipo inválido.") };
        await using var connection = await source.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition($"UPDATE {table} SET atencao_motoboy=@Attention, updated_at=NOW() WHERE id=@Id AND id_estabelecimento=@Store AND deleted_at IS NULL",
            new { Store = store, Id = id, Attention = attention }, cancellationToken: ct)) == 1;
    }

    public static async Task ApplyAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid store, IReadOnlyList<PedidoItemDto> items, CancellationToken ct = default)
    {
        var products = items.Where(i => i.ProdutoId.HasValue).Select(i => i.ProdutoId!.Value).Distinct().ToArray();
        var extras = items.SelectMany(i => i.Adicionais).Where(i => i.Id.HasValue).Select(i => i.Id!.Value).Distinct().ToArray();
        // Pedidos legados sem IDs não são classificados por nome ou por flags do cliente.
        foreach (var item in items) { item.AtencaoMotoboy = false; foreach (var extra in item.Adicionais) extra.AtencaoMotoboy = false; }
        if (products.Length == 0 && extras.Length == 0) return;
        using var rows = await connection.QueryMultipleAsync(new CommandDefinition("""
SELECT id FROM cardapio_produto WHERE id_estabelecimento=@Store AND deleted_at IS NULL AND atencao_motoboy AND id=ANY(@Products);
SELECT id FROM cardapio_grupo_adicional WHERE id_estabelecimento=@Store AND deleted_at IS NULL AND atencao_motoboy AND id=ANY(@Extras)
UNION
SELECT i.id FROM cardapio_grupo_adicional_item i JOIN cardapio_grupo_adicional g ON g.id=i.id_grupo
 WHERE g.id_estabelecimento=@Store AND g.deleted_at IS NULL AND g.atencao_motoboy AND i.id=ANY(@Extras);
""", new { Store = store, Products = products, Extras = extras }, transaction, cancellationToken: ct));
        var markedProducts = (await rows.ReadAsync<Guid>()).ToHashSet();
        var markedExtras = (await rows.ReadAsync<Guid>()).ToHashSet();
        foreach (var item in items)
        {
            item.AtencaoMotoboy = item.ProdutoId.HasValue && markedProducts.Contains(item.ProdutoId.Value);
            foreach (var extra in item.Adicionais) extra.AtencaoMotoboy = extra.Id.HasValue && markedExtras.Contains(extra.Id.Value);
        }
    }
}
