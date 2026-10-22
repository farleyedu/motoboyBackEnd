using System.Collections.Concurrent;
using Dapper;
using Npgsql;

namespace APIBack.Integrations.Ifood.Orders;

/// <summary>
/// Sondas de schema do iFood. O schema não muda em runtime: depois do primeiro "sim" a consulta sai do caminho de cada
/// request. O "não" não é guardado, para a primeira migração aplicada valer sem reiniciar. A chave inclui a connection string
/// (que carrega o search_path), então bancos/schemas diferentes, como os dos testes de integração, não se contaminam.
/// </summary>
internal static class IfoodSchemaProbe
{
    private static readonly ConcurrentDictionary<(string Connection, string Probe), bool> Known = new();

    public static Task<bool> OrdersAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction) =>
        Probe(connection, transaction, "orders", "SELECT to_regclass('ifood_order_links') IS NOT NULL AND to_regclass('ifood_order_snapshots') IS NOT NULL");

    public static Task<bool> CommandsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction) =>
        Probe(connection, transaction, "commands", "SELECT to_regclass('ifood_command_attempts') IS NOT NULL");

    public static Task<bool> ItemsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction) =>
        Probe(connection, transaction, "items", "SELECT to_regclass('ifood_order_items') IS NOT NULL");

    public static Task<bool> CompletionsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction) =>
        Probe(connection, transaction, "completions", "SELECT to_regclass('ifood_delivery_completions') IS NOT NULL");

    /// <summary>Vínculo item iFood → produto do cardápio (também exige o que o JOIN de leitura usa: cardápio, categoria e aviso de bag).</summary>
    public static Task<bool> LinksAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction) =>
        Probe(connection, transaction, "links", """
SELECT to_regclass('ifood_produto_vinculo') IS NOT NULL AND to_regclass('cardapio_produto') IS NOT NULL AND to_regclass('cardapio_categoria') IS NOT NULL
AND EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=current_schema() AND table_name='cardapio_produto' AND column_name='atencao_motoboy')
""");

    public static Task<bool> BlockedCodeAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction) =>
        Probe(connection, transaction, "blocked-code", "SELECT to_regprocedure('ifood_delivery_blocked_code(uuid,integer,boolean)') IS NOT NULL");

    private static async Task<bool> Probe(NpgsqlConnection connection, NpgsqlTransaction? transaction, string probe, string sql)
    {
        var key = (connection.ConnectionString, probe);
        if (Known.ContainsKey(key)) return true;
        var exists = await connection.ExecuteScalarAsync<bool>(sql, transaction: transaction);
        if (exists) Known[key] = true;
        return exists;
    }
}
