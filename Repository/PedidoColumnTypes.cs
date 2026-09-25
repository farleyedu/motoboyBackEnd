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

        private static volatile bool _routeRulesKnown;

        /// <summary>
        /// True quando a migration 20260927_01 (Fase 4: lock e retorno a loja) foi aplicada. So cacheia o
        /// "sim": enquanto ausente, reconsulta, e a API antes da migration segue como antes.
        /// </summary>
        public static async Task<bool> HasRouteRulesSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction)
        {
            if (_routeRulesKnown) return true;
            var present = await connection.ExecuteScalarAsync<bool>(@"
SELECT (SELECT COUNT(*) FROM information_schema.columns
         WHERE table_schema = current_schema()
           AND ((table_name = 'delivery_route_stops' AND column_name = 'locked')
             OR (table_name = 'delivery_motoboy_route' AND column_name = 'route_state')
             OR (table_name = 'delivery_settings' AND column_name = 'store_return_radius_m'))) = 3;", transaction: transaction);
            if (present) _routeRulesKnown = true;
            return present;
        }

        private static volatile bool _coreSchemaKnown;

        /// <summary>
        /// True quando as migracoes da Fase 2 foram aplicadas (colunas novas de pedido e pedido_item).
        /// So cacheia o "sim": enquanto ausente, reconsulta, e o deploy da API antes da migration
        /// nao derruba o fluxo antigo de criacao de pedido.
        /// </summary>
        public static async Task<bool> HasCoreSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction)
        {
            if (_coreSchemaKnown) return true;
            var present = await connection.ExecuteScalarAsync<bool>(@"
SELECT (SELECT COUNT(*) FROM information_schema.columns
         WHERE table_schema = current_schema() AND table_name = 'pedido'
           AND column_name IN ('origem', 'origem_ref', 'conversa_id', 'subtotal', 'taxa_entrega', 'desconto')) = 6
   AND to_regclass('pedido_item') IS NOT NULL;", transaction: transaction);
            if (present) _coreSchemaKnown = true;
            return present;
        }
    }
}
