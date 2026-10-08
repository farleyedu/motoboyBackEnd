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
        private static readonly ConcurrentDictionary<string, (string DataType, int? MaxLength)> Cache = new();

        public static async Task<string> LocalNowSqlAsync(
            NpgsqlConnection connection, NpgsqlTransaction? transaction, string column, string? minutesParameter = null)
        {
            if (!Cache.TryGetValue(column, out var info))
            {
                var row = await connection.QuerySingleOrDefaultAsync<(string? DataType, int? MaxLength)>(@"
SELECT data_type AS DataType, character_maximum_length AS MaxLength
  FROM information_schema.columns
 WHERE table_schema = current_schema()
   AND table_name = 'pedido'
   AND column_name = @Column;", new { Column = column }, transaction);
                info = (row.DataType ?? "text", row.MaxLength);
                Cache[column] = info;
            }

            return DeliveryRules.LocalNowSql(info.DataType, minutesParameter, info.MaxLength);
        }

        private static volatile bool _routeRulesKnown;

        /// <summary>
        /// True quando a migration 20260927_01 (Fase 4: lock e retorno a loja) foi aplicada. So cacheia o
        /// "sim": enquanto ausente, reconsulta, e a API antes da migration segue como antes.
        /// </summary>
        public static async Task<bool> HasRouteRulesSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken = default)
        {
            if (_routeRulesKnown) return true;
            var present = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(@"
SELECT (SELECT COUNT(*) FROM information_schema.columns
         WHERE table_schema = current_schema()
           AND ((table_name = 'delivery_route_stops' AND column_name = 'locked')
             OR (table_name = 'delivery_motoboy_route' AND column_name = 'route_state')
             OR (table_name = 'delivery_settings' AND column_name = 'store_return_radius_m'))) = 3;", transaction: transaction, commandTimeout: 10, cancellationToken: cancellationToken));
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

        private static volatile bool _operacaoSchemaKnown;

        /// <summary>
        /// True quando a migration 20260928_04 (Fase 2 da reconstrucao de Configuracoes: zonas e
        /// operacao) foi aplicada.
        /// </summary>
        public static async Task<bool> HasOperacaoSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction)
        {
            if (_operacaoSchemaKnown) return true;
            var present = await connection.ExecuteScalarAsync<bool>(@"
SELECT (SELECT COUNT(*) FROM information_schema.columns
         WHERE table_schema = current_schema() AND table_name = 'delivery_settings'
           AND column_name IN ('auto_confirmar_pedidos', 'autoatribuir_motoboy', 'bloquear_pedidos_fora_horario',
                                'retirada_balcao_ativa', 'retirada_tempo_preparo_min')) = 5;", transaction: transaction);
            if (present) _operacaoSchemaKnown = true;
            return present;
        }

        private static volatile bool _ofertaSchemaKnown;

        /// <summary>
        /// True quando a migration 20261002_02 (confirmacao do motoboy: ofertas de rota) foi aplicada.
        /// So cacheia o "sim"; antes dela a atribuicao continua virando entrega na hora.
        /// </summary>
        public static async Task<bool> HasOfertaSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken = default)
        {
            if (_ofertaSchemaKnown) return true;
            var present = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(@"
SELECT (SELECT COUNT(*) FROM information_schema.columns
         WHERE table_schema = current_schema()
           AND ((table_name = 'delivery_route_stops' AND column_name IN ('offer_id', 'offered_at_utc'))
             OR (table_name = 'delivery_settings' AND column_name IN ('require_motoboy_acceptance', 'offer_timeout_minutes')))) = 4;", transaction: transaction, commandTimeout: 10, cancellationToken: cancellationToken));
            if (present) _ofertaSchemaKnown = true;
            return present;
        }

        private static volatile bool _encerramentoSchemaKnown;

        /// <summary>
        /// True quando a migration 20261002_01 (encerramento automatico: historico de encerramentos e
        /// parametros) foi aplicada. So cacheia o "sim".
        /// </summary>
        public static async Task<bool> HasEncerramentoSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction)
        {
            if (_encerramentoSchemaKnown) return true;
            var present = await connection.ExecuteScalarAsync<bool>(@"
SELECT to_regclass('pedido_encerramento') IS NOT NULL
   AND (SELECT COUNT(*) FROM information_schema.columns
         WHERE table_schema = current_schema() AND table_name = 'delivery_settings'
           AND column_name IN ('encerramento_auto_ativo', 'encerramento_auto_horas')) = 2;", transaction: transaction);
            if (present) _encerramentoSchemaKnown = true;
            return present;
        }
    }
}
