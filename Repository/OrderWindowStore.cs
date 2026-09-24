using System;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    /// <summary>
    /// Leitura da janela de pedidos do mapa. Fica separada da leitura dos demais parametros
    /// de proposito: se a migration da coluna ainda nao rodou, o mapa cai no padrao em vez de
    /// quebrar -- e a leitura acontece fora de transacao, porque um erro de SQL dentro de uma
    /// transacao do Postgres a deixaria abortada para o resto do comando.
    /// </summary>
    internal static class OrderWindowStore
    {
        /// <summary>Prazo padrao (minutos) salvo, ou null se nao ha valor ou a coluna ainda nao existe.</summary>
        public static async Task<int?> ReadDefaultDeliveryMinutesAsync(NpgsqlConnection connection, Guid estabelecimentoId)
        {
            try
            {
                return await connection.ExecuteScalarAsync<int?>(
                    "SELECT default_delivery_minutes FROM delivery_settings WHERE estabelecimento_id = @EstabelecimentoId;",
                    new { EstabelecimentoId = estabelecimentoId });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedColumn)
            {
                return null;
            }
        }

        public static async Task<OrderWindowDto> ReadAsync(NpgsqlConnection connection, Guid estabelecimentoId)
        {
            try
            {
                var raw = await connection.ExecuteScalarAsync<string?>(
                    "SELECT order_window::text FROM delivery_settings WHERE estabelecimento_id = @EstabelecimentoId;",
                    new { EstabelecimentoId = estabelecimentoId });
                return OrderWindowRules.Parse(raw);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedColumn)
            {
                return OrderWindowRules.Default();
            }
        }
    }
}
