using System.Collections.Concurrent;
using System.Threading.Tasks;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    /// <summary>
    /// Descobre (uma vez por processo) o tipo real das colunas de horario da tabela
    /// legada `pedido`, que nao e criada pelas migrations do delivery. Com o tipo em
    /// maos, as escritas usam a expressao de "agora" certa para cada coluna.
    /// </summary>
    internal static class PedidoColumnTypes
    {
        private static readonly ConcurrentDictionary<string, string> Cache = new();

        public static async Task<string> LocalNowSqlAsync(
            NpgsqlConnection connection, NpgsqlTransaction? transaction, string column, string? minutesParameter = null)
        {
            if (!Cache.TryGetValue(column, out var dataType))
            {
                dataType = await connection.ExecuteScalarAsync<string?>(@"
SELECT data_type
  FROM information_schema.columns
 WHERE table_schema = current_schema()
   AND table_name = 'pedido'
   AND column_name = @Column;", new { Column = column }, transaction) ?? "text";
                Cache[column] = dataType;
            }

            return DeliveryRules.LocalNowSql(dataType, minutesParameter);
        }
    }
}
