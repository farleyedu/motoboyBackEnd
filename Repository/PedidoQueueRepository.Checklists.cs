using APIBack.DTOs.Delivery;
using APIBack.Service;
using Dapper;
using Npgsql;
using System.Text.Json;

namespace APIBack.Repository;

public sealed partial class PedidoQueueRepository
{
    private static async Task SaveChecklistAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid store, int rider, long stop, string stage, DeliveryChecklist manifest, DeliveryChecklistConfirmation confirmation, CancellationToken ct = default)
    {
        await connection.ExecuteAsync(new CommandDefinition("""
INSERT INTO delivery_order_item_checks(stop_id,estabelecimento_id,motoboy_id,stage,manifest,confirmation)
 VALUES(@Stop,@Store,@Rider,@Stage,CAST(@Manifest AS jsonb),CAST(@Confirmation AS jsonb))
 ON CONFLICT(stop_id,stage) DO UPDATE SET manifest=EXCLUDED.manifest,confirmation=EXCLUDED.confirmation,confirmed_at_utc=NOW()
""", new { Stop=stop, Store=store, Rider=rider, Stage=stage, Manifest=JsonSerializer.Serialize(manifest), Confirmation=JsonSerializer.Serialize(confirmation) }, transaction, cancellationToken: ct));
    }
    private static async Task<DeliveryChecklist> ReadChecklistAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid store, int pedido, CancellationToken ct = default)
    {
        List<PedidoItemDto> items = new();
        if (await PedidoColumnTypes.HasCoreSchemaAsync(connection, transaction))
        {
            var rows = await connection.QueryAsync<PedidoConsultaRepository.ItemRow>(new CommandDefinition("""
SELECT i.id::text AS ItemId, i.produto_id AS ProdutoId, i.nome AS Nome,
       i.quantidade AS Quantidade, i.preco_unitario AS PrecoUnitario,
       i.observacao AS Observacao, i.adicionais::text AS Adicionais, cp.imagem_url AS ImagemUrl
  FROM pedido_item i JOIN pedido p ON p.id=i.pedido_id
  LEFT JOIN cardapio_produto cp ON cp.id=i.produto_id AND cp.id_estabelecimento=@Store
 WHERE p.id=@Pedido AND p.id_estabelecimento=@Store ORDER BY i.ordem,i.id
""", new { Pedido = pedido, Store = store }, transaction, cancellationToken: ct));
            items = rows.Select(PedidoConsultaRepository.ToItem).ToList();
        }
        if (items.Count == 0)
        {
            var raw = await connection.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT items::text FROM pedido WHERE id=@Pedido AND id_estabelecimento=@Store", new { Pedido = pedido, Store = store }, transaction, cancellationToken: ct));
            items = LegacyItemsParser.Parse(raw);
        }
        return DeliveryChecklistRules.Build(items);
    }
}
