using APIBack.DTOs;
using APIBack.Model;
using APIBack.Model.Enum;
using APIBack.Repository.Interface;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Npgsql;


namespace APIBack.Repository
{
    public class PedidoRepository : IPedidoRepository
    {
        private readonly string _connectionString;

        public PedidoRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        public IEnumerable<Pedido> GetPedidos(Guid estabelecimentoId)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            {
                return connection.Query<Pedido>(
                    "SELECT * FROM pedido WHERE id_estabelecimento = @EstabelecimentoId AND COALESCE(status_pedido, 1) <> 6",
                    new { EstabelecimentoId = estabelecimentoId }).ToList();
            }
        }

        public IEnumerable<PedidoDTOs> GetPedidosMaps(Guid estabelecimentoId)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            {
                var sql = @"
                SELECT
    p.id,
    p.nome_cliente           AS ""NomeCliente"",
    p.endereco_entrega       AS ""EnderecoEntrega"",
    p.id_ifood               AS ""IdIfood"",
    p.telefone_cliente       AS ""TelefoneCliente"",
    p.data_pedido            AS ""DataPedido"",
    p.status_pedido          AS ""StatusPedido"",   -- ⭐️ nome idêntico ao DTO
    p.horario_pedido         AS ""HorarioPedido"",
    p.previsao_entrega       AS ""PrevisaoEntrega"",
    p.horario_saida          AS ""HorarioSaida"",
    p.horario_entrega        AS ""HorarioEntrega"",
    p.items                  AS ""Items"",
    p.value                  AS ""Value"",
    p.region                 AS ""Region"",
    p.latitude               AS ""Latitude"",
    p.longitude              AS ""Longitude"",

    -- Campos do motoboy (após o splitOn)
    m.id                     AS motoboyid,
    m.nome                   AS nome,
    m.avatar                 AS avatar,
    m.status                 AS status
FROM pedido p
LEFT JOIN motoboy m ON m.id = p.motoboy_responsavel
WHERE p.id_estabelecimento = @EstabelecimentoId
  AND COALESCE(p.status_pedido, 1) <> 6;  -- 6 = Rascunho: nao aparece em listas
";

                var pedidos = connection.Query<PedidoDTOs, MotoboyDTO, PedidoDTOs>(
                   sql,
                   (pedido, motoboy) =>
                   {
                       pedido.MotoboyResponsavel = motoboy;
                       return pedido;
                   },
                   new { EstabelecimentoId = estabelecimentoId },
                   splitOn: "motoboyid"
               );
                return pedidos;
            }
        }


        public IEnumerable<Pedido> GetPedidosPorMotoboy(int motoboyId, Guid estabelecimentoId)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            {
                var sql = "SELECT * FROM Pedido WHERE motoboy_responsavel = @MotoboyId AND id_estabelecimento = @EstabelecimentoId";
                return connection.Query<Pedido>(sql, new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }).ToList();
            }
        }

        public bool InserirPedidosIfood(PedidoCapturado pedido, Guid estabelecimentoId)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            // Idempotencia: um webhook repetido para o mesmo pedido iFood/estabelecimento
            // nao deve duplicar a linha. Reforcado tambem por indice unico parcial
            // (ux_pedido_id_ifood) adicionado na migracao 20260723_04_constraints.sql.
            if (!string.IsNullOrWhiteSpace(pedido.DisplayId))
            {
                var existing = connection.ExecuteScalar<int?>(
                    "SELECT id FROM pedido WHERE id_ifood = @IdIfood AND id_estabelecimento = @EstabelecimentoId LIMIT 1;",
                    new { IdIfood = pedido.DisplayId, EstabelecimentoId = estabelecimentoId });
                if (existing.HasValue)
                {
                    return false;
                }
            }

            var sql = @"
    INSERT INTO pedido (
        nome_cliente, endereco_entrega, id_ifood, telefone_cliente, data_pedido,
        status_pedido, horario_entrega, items, value, region,
        latitude, longitude, horario_pedido, previsao_entrega, horario_saida, localizador,
        entrega_rua, entrega_numero, entrega_bairro, entrega_cidade, entrega_estado, entrega_cep,
        documento_cliente, tipo_pagamento, id_estabelecimento
    )
    VALUES (
        @NomeCliente, @EnderecoEntrega, @IdIfood, @TelefoneCliente, @DataPedido,
        @StatusPedido, @HorarioEntrega, @Items, @Value, @Region,
        @Latitude, @Longitude, @HorarioPedido, @PrevisaoEntrega, @HorarioSaida, @Localizador,
        @EntregaRua, @EntregaNumero, @EntregaBairro, @EntregaCidade, @EntregaEstado, @EntregaCep,
        @DocumentoCliente, @TipoPagamento, @EstabelecimentoId
    )
    ON CONFLICT (id_ifood) WHERE id_ifood IS NOT NULL DO NOTHING;";

            try
            {
                var rows = connection.Execute(sql, new
                {
                    PedidoIdIfood = pedido.DisplayId,
                    NomeCliente = pedido.Cliente.Nome,
                    EnderecoEntrega = $"{pedido.Endereco.Rua}, {pedido.Endereco.Numero}",
                    IdIfood = pedido.DisplayId,
                    TelefoneCliente = pedido.Cliente.Telefone,
                    DataPedido = pedido.CriadoEm,
                    StatusPedido = StatusPedido.Pendente,

                    HorarioEntrega = pedido.HorarioEntrega.HasValue
                        ? pedido.HorarioEntrega.Value
                        : (DateTime?)null,

                    HorarioSaida = pedido.HorarioSaida.HasValue
                        ? pedido.HorarioSaida.Value
                        : (DateTime?)null,

                    Items = JsonSerializer.Serialize(pedido.Itens),
                    Value = pedido.Itens.Sum(i => i.PrecoTotal ?? 0),
                    Region = pedido.Endereco.Bairro,
                    Latitude = pedido.Coordenadas.Latitude,
                    Longitude = pedido.Coordenadas.Longitude,
                    HorarioPedido = pedido.CriadoEm,
                    PrevisaoEntrega = pedido.PrevisaoEntrega,
                    Localizador = pedido.Localizador,
                    EntregaRua = pedido.Endereco.Rua,
                    EntregaNumero = pedido.Endereco.Numero,
                    EntregaBairro = pedido.Endereco.Bairro,
                    EntregaCidade = pedido.Endereco.Cidade,
                    EntregaEstado = pedido.Endereco.Estado,
                    EntregaCep = pedido.Endereco.Cep,
                    DocumentoCliente = pedido.Cliente.Documento,
                    TipoPagamento = pedido.TipoPagamento,
                    EstabelecimentoId = estabelecimentoId
                });
                if (rows > 0 && PedidoColumnTypes.HasCoreSchemaAsync(connection, null).GetAwaiter().GetResult())
                {
                    // Origem do pedido no nucleo (Fase 2): o iFood entra por captura assistida.
                    connection.Execute(
                        "UPDATE pedido SET origem = 'ifood', origem_ref = @IdIfood WHERE id_ifood = @IdIfood AND id_estabelecimento = @EstabelecimentoId AND origem_ref IS NULL;",
                        new { IdIfood = pedido.DisplayId, EstabelecimentoId = estabelecimentoId });
                }
                return rows > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Erro ao inserir pedido {pedido.Id}: {ex.Message}");
                throw;
            }
        }



        public EnviarPedidosParaRotaDTO? GetPedidosId(int id, Guid estabelecimentoId)
        {
            using var connection = new NpgsqlConnection(_connectionString);

            const string sql = @"
        SELECT p.*, m.id as MotoboyId, m.nome as NomeMotoboy, m.avatar, m.status
        FROM pedido p
        LEFT JOIN motoboy m ON m.id = p.motoboy_responsavel
        WHERE p.id = @Id AND p.id_estabelecimento = @EstabelecimentoId";

            var result = connection.Query<Pedido, MotoboyDTO, (Pedido, MotoboyDTO)>(
                sql,
                (pedido, motoboy) => (pedido, motoboy),
                new { Id = id, EstabelecimentoId = estabelecimentoId },
                splitOn: "MotoboyId"
            );

            var tuple = result.FirstOrDefault();
            if (tuple.Item1 == null) return null;

            return new EnviarPedidosParaRotaDTO
            {
                PedidosIds = tuple.Item1.Id != null ? new List<int> { tuple.Item1.Id.Value } : new List<int>(),
                MotoboyResponsavel = tuple.Item2.Id,
                StatusPedido = tuple.Item1.StatusPedido != null ? (StatusPedido)tuple.Item1.StatusPedido : StatusPedido.Pendente,
                HorarioSaida = DateTime.UtcNow.ToString("HH:mm:ss")
            };
        }






        /// <summary>
        /// Obtém pedido completo com todos os detalhes para o endpoint riderlink
        /// Utiliza QueryMultiple para otimizar as consultas ao banco
        /// </summary>
        public async Task<PedidoCompletoResponse?> GetPedidoCompleto(int id, Guid estabelecimentoId)
        {
            using var connection = new NpgsqlConnection(_connectionString);

            // Query principal: pedido + cliente + endereço + pagamento + motoboy + coordenadas
            const string sqlPedido = @"
                SELECT 
                    p.id,
                    p.id_ifood AS ""IdIfood"",
                    p.nome_cliente AS ""NomeCliente"",
                    p.telefone_cliente AS ""TelefoneCliente"",
                    p.endereco_entrega AS ""EnderecoEntrega"",
                    COALESCE(p.entrega_bairro, p.region) AS ""Bairro"",
                    CAST(p.latitude AS FLOAT8) AS ""Latitude"",
                    CAST(p.longitude AS FLOAT8) AS ""Longitude"",
                    COALESCE(p.value, 0) AS ""ValorTotal"",
                    COALESCE(p.tipo_pagamento, 'a_receber') AS ""TipoPagamento"",
                    CASE 
                        WHEN p.tipo_pagamento = 'pagoApp' THEN 'pago'
                        ELSE 'a_receber'
                    END AS ""StatusPagamento"",
                    NULL AS ""Troco"", -- Campo não existe na tabela atual
                    p.horario_pedido,
                    p.data_pedido AS ""DataPedido"",
                    p.status_pedido,
                    m.nome AS ""MotoboyResponsavel"",
                    0.0 AS ""DistanciaKm"", -- Calcular posteriormente se necessário
                    p.localizador AS ""CodigoEntrega"",
                    NULL AS ""Observacoes"" -- Campo não existe na tabela atual
                FROM pedido p
                LEFT JOIN motoboy m ON m.id = p.motoboy_responsavel
                WHERE p.id = @Id AND p.id_estabelecimento = @EstabelecimentoId
                  AND COALESCE(p.status_pedido, 1) <> 6;  -- rascunho nao e exposto

                -- Query para itens do pedido (parseando texto simples)
                SELECT 
                    ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS ""Id"",
                    TRIM(item_name) AS ""Nome"",
                    1 AS ""Quantidade"",
                    ROUND(p.value / GREATEST(array_length(string_to_array(p.items, ','), 1), 1), 2) AS ""Valor"",
                    CASE 
                        WHEN LOWER(TRIM(item_name)) LIKE '%bebida%' OR LOWER(TRIM(item_name)) LIKE '%refrigerante%' 
                             OR LOWER(TRIM(item_name)) LIKE '%suco%' OR LOWER(TRIM(item_name)) LIKE '%água%'
                             OR LOWER(TRIM(item_name)) LIKE '%coca%' OR LOWER(TRIM(item_name)) LIKE '%guaraná%'
                        THEN 'bebida'
                        ELSE 'comida'
                    END AS ""Tipo"",
                    NULL AS ""Observacoes""
                FROM pedido p
                CROSS JOIN LATERAL unnest(string_to_array(p.items, ',')) AS item_name
                WHERE p.id = @Id;
                
                -- Query para timeline (simulada baseada nos status do pedido)
                SELECT 
                    ROW_NUMBER() OVER (ORDER BY evento_ordem) AS ""Id"",
                    evento AS ""Evento"",
                    horario AS ""Horario"",
                    local AS ""Local"",
                    status AS ""Status""
                FROM (
                    SELECT 1 AS evento_ordem, 'Pedido Criado' AS evento, 
                           CASE 
                               WHEN p.data_pedido IS NOT NULL THEN TO_CHAR(p.data_pedido::timestamp, 'HH24:MI')
                               ELSE '--:--'
                           END AS horario,
                           'Sistema' AS local,
                           'concluido' AS status
                    FROM pedido p WHERE p.id = @Id
                    
                    UNION ALL
                    
                    SELECT 2 AS evento_ordem, 'Atribuído ao Motoboy' AS evento,
                           CASE 
                               WHEN p.horario_saida IS NOT NULL THEN TO_CHAR(p.horario_saida::timestamp, 'HH24:MI')
                               ELSE '--:--'
                           END AS horario,
                           COALESCE(m.nome, 'Não atribuído') AS local,
                           CASE WHEN p.motoboy_responsavel IS NOT NULL THEN 'concluido' ELSE 'pendente' END AS status
                    FROM pedido p 
                    LEFT JOIN motoboy m ON m.id = p.motoboy_responsavel
                    WHERE p.id = @Id
                    
                    UNION ALL
                    
                    SELECT 3 AS evento_ordem, 'Em Entrega' AS evento,
                           CASE 
                               WHEN p.horario_saida IS NOT NULL THEN TO_CHAR(p.horario_saida::timestamp, 'HH24:MI')
                               ELSE '--:--'
                           END AS horario,
                           'Em trânsito' AS local,
                           CASE 
                               WHEN p.status_pedido = 2 THEN 'em_andamento' -- 2 = EmRota: e a entrega atual
                               WHEN p.status_pedido = 3 THEN 'concluido'    -- 3 = Concluido (StatusPedido); 4 agora e Cancelado
                               ELSE 'pendente' 
                           END AS status
                    FROM pedido p WHERE p.id = @Id
                    
                    UNION ALL
                    
                    SELECT 4 AS evento_ordem, 'Entregue' AS evento,
                           CASE 
                               WHEN p.horario_entrega IS NOT NULL THEN TO_CHAR(p.horario_entrega::timestamp, 'HH24:MI')
                               ELSE '--:--'
                           END AS horario,
                           p.endereco_entrega AS local,
                            CASE WHEN p.status_pedido = 3 THEN 'concluido' ELSE 'pendente' END AS status -- entregue = Concluido (3), nao 4
                    FROM pedido p WHERE p.id = @Id
                ) timeline_data
                ORDER BY evento_ordem;
            ";

            try
            {
                using var multi = await connection.QueryMultipleAsync(sqlPedido, new { Id = id, EstabelecimentoId = estabelecimentoId });

                // Lê o pedido principal
                var pedidoData = await multi.ReadFirstOrDefaultAsync<dynamic>();
                if (pedidoData == null)
                    return null;

                // Lê os itens
                var itens = (await multi.ReadAsync<ItemPedidoDto>()).ToList();
                
                // Lê a timeline
                var timeline = (await multi.ReadAsync<TimelineDto>()).ToList();

                // Mapeia o status do pedido
                string statusPedido = MapearStatusPedido((int)pedidoData.status_pedido);
                
                // Formata horários
                string horarioPedido = "--:--";
                string horarioFormatado = "--:--";
                string dataPedido = DateTime.UtcNow.ToString("O"); // ISO format
                
                if (pedidoData.horario_pedido != null)
                {
                    if (DateTime.TryParse(pedidoData.horario_pedido.ToString(), out DateTime horario))
                    {
                        horarioPedido = horario.ToString("HH:mm");
                        horarioFormatado = horario.ToString("HH:mm");
                    }
                }
                
                if (pedidoData.DataPedido != null)
                {
                    if (DateTime.TryParse(pedidoData.DataPedido.ToString(), out DateTime data))
                    {
                        dataPedido = data.ToString("O"); // ISO format
                    }
                }

                // Constrói o objeto de resposta
                var response = new PedidoCompletoResponse
                {
                    Id = (int)pedidoData.id,
                    IdIfood = pedidoData.IdIfood?.ToString(),
                    NomeCliente = pedidoData.NomeCliente?.ToString() ?? "",
                    TelefoneCliente = pedidoData.TelefoneCliente?.ToString() ?? "",
                    EnderecoEntrega = pedidoData.EnderecoEntrega?.ToString() ?? "",
                    Bairro = pedidoData.Bairro?.ToString() ?? "",
                    Coordinates = new CoordinatesDto
                    {
                        Lat = Convert.ToDouble(pedidoData.Latitude ?? 0),
                        Lng = Convert.ToDouble(pedidoData.Longitude ?? 0)
                    },
                    ValorTotal = Convert.ToDecimal(pedidoData.ValorTotal ?? 0),
                    TipoPagamento = pedidoData.TipoPagamento?.ToString() ?? "a_receber",
                    StatusPagamento = pedidoData.StatusPagamento?.ToString() ?? "a_receber",
                    Troco = pedidoData.Troco != null ? Convert.ToDecimal(pedidoData.Troco) : null,
                    Itens = itens,
                    HorarioPedido = horarioPedido,
                    HorarioFormatado = horarioFormatado,
                    DataPedido = dataPedido,
                    StatusPedido = statusPedido,
                    MotoboyResponsavel = pedidoData.MotoboyResponsavel?.ToString(),
                    DistanciaKm = Convert.ToDouble(pedidoData.DistanciaKm ?? 0),
                    Timeline = timeline,
                    CodigoEntrega = pedidoData.CodigoEntrega?.ToString(),
                    Observacoes = pedidoData.Observacoes?.ToString()
                };

                return response;
            }
            catch (Exception ex)
            {
                // Log estruturado do erro (implementar logger se necessário)
                Console.WriteLine($"❌ Erro ao buscar pedido completo {id}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Obtém todos os pedidos completos com todos os detalhes
        /// Utiliza QueryMultiple para otimizar as consultas ao banco
        /// </summary>
        public async Task<List<PedidoCompletoResponse>> GetTodosPedidosCompletos(Guid estabelecimentoId)
        {
            using var connection = new NpgsqlConnection(_connectionString);

            // Query principal: todos os pedidos + cliente + endereço + pagamento + motoboy + coordenadas
            const string sqlPedidos = @"
                SELECT 
                    p.id,
                    p.id_ifood AS ""IdIfood"",
                    p.nome_cliente AS ""NomeCliente"",
                    p.telefone_cliente AS ""TelefoneCliente"",
                    p.endereco_entrega AS ""EnderecoEntrega"",
                    COALESCE(p.entrega_bairro, p.region) AS ""Bairro"",
                    CAST(p.latitude AS FLOAT8) AS ""Latitude"",
                    CAST(p.longitude AS FLOAT8) AS ""Longitude"",
                    COALESCE(p.value, 0) AS ""ValorTotal"",
                    COALESCE(p.tipo_pagamento, 'a_receber') AS ""TipoPagamento"",
                    CASE 
                        WHEN p.tipo_pagamento = 'pagoApp' THEN 'pago'
                        ELSE 'a_receber'
                    END AS ""StatusPagamento"",
                    NULL AS ""Troco"", -- Campo não existe na tabela atual
                    p.horario_pedido,
                    p.data_pedido AS ""DataPedido"",
                    p.status_pedido,
                    m.nome AS ""MotoboyResponsavel"",
                    0.0 AS ""DistanciaKm"", -- Calcular posteriormente se necessário
                    p.localizador AS ""CodigoEntrega"",
                    NULL AS ""Observacoes"" -- Campo não existe na tabela atual
                FROM pedido p
                LEFT JOIN motoboy m ON m.id = p.motoboy_responsavel
                WHERE p.id_estabelecimento = @EstabelecimentoId
                  AND COALESCE(p.status_pedido, 1) <> 6  -- rascunho nao e exposto
                ORDER BY p.data_pedido DESC;

                -- Query para todos os itens dos pedidos (parseando JSON)
                SELECT 
                    jr.id AS ""PedidoId"",
                    ROW_NUMBER() OVER (PARTITION BY jr.id ORDER BY (SELECT NULL)) AS ""Id"",
                    item->>'nome' AS ""Nome"",
                    CAST(COALESCE(item->>'quantidade', '1') AS INTEGER) AS ""Quantidade"",
                    CAST(COALESCE(item->>'precoTotal', item->>'preco', '0') AS DECIMAL) AS ""Valor"",
                    CASE 
                        WHEN LOWER(item->>'nome') LIKE '%bebida%' OR LOWER(item->>'nome') LIKE '%refrigerante%' 
                             OR LOWER(item->>'nome') LIKE '%suco%' OR LOWER(item->>'nome') LIKE '%água%'
                        THEN 'bebida'
                        ELSE 'comida'
                    END AS ""Tipo"",
                    item->>'observacoes' AS ""Observacoes""
                FROM (
                    SELECT id, items, value FROM pedido
                    WHERE items IS NOT NULL AND LEFT(TRIM(items), 1) = '['
                ) jr
                CROSS JOIN LATERAL jsonb_array_elements(jr.items::jsonb) AS item
                
                UNION ALL
                
                SELECT 
                    cr.id AS ""PedidoId"",
                    ROW_NUMBER() OVER (PARTITION BY cr.id ORDER BY (SELECT NULL)) AS ""Id"",
                    TRIM(item_name)                            AS ""Nome"",
                    1                                          AS ""Quantidade"",
                    ROUND(cr.value / GREATEST(array_length(string_to_array(cr.items, ','), 1), 1), 2) AS ""Valor"",
                    CASE 
                        WHEN LOWER(TRIM(item_name)) LIKE '%bebida%' OR LOWER(TRIM(item_name)) LIKE '%refrigerante%' 
                             OR LOWER(TRIM(item_name)) LIKE '%suco%' OR LOWER(TRIM(item_name)) LIKE '%ǭgua%'
                             OR LOWER(TRIM(item_name)) LIKE '%coca%' OR LOWER(TRIM(item_name)) LIKE '%guaranǭ%'
                        THEN 'bebida'
                        ELSE 'comida'
                    END                                       AS ""Tipo"",
                    NULL                                      AS ""Observacoes""
                FROM (
                    SELECT id, items, value FROM pedido
                    WHERE items IS NOT NULL AND LEFT(TRIM(items), 1) <> '['
                ) cr
                CROSS JOIN LATERAL unnest(string_to_array(cr.items, ',')) AS item_name;
                
                -- Query para timeline de todos os pedidos (simulada baseada nos status)
                SELECT 
                    id AS ""PedidoId"",
                    ROW_NUMBER() OVER (PARTITION BY id ORDER BY evento_ordem) AS ""Id"",
                    evento AS ""Evento"",
                    horario AS ""Horario"",
                    local AS ""Local"",
                    status AS ""Status""
                FROM (
                    SELECT p.id, 1 AS evento_ordem, 'Pedido Criado' AS evento, 
                           CASE 
                               WHEN p.data_pedido IS NOT NULL THEN TO_CHAR(p.data_pedido::timestamp, 'HH24:MI')
                               ELSE '--:--'
                           END AS horario,
                           'Sistema' AS local,
                           'concluido' AS status
                    FROM pedido p
                    
                    UNION ALL
                    
                    SELECT p.id, 2 AS evento_ordem, 'Atribuído ao Motoboy' AS evento,
                           CASE 
                               WHEN p.horario_saida IS NOT NULL THEN TO_CHAR(p.horario_saida::timestamp, 'HH24:MI')
                               ELSE '--:--'
                           END AS horario,
                           COALESCE(m.nome, 'Não atribuído') AS local,
                           CASE WHEN p.motoboy_responsavel IS NOT NULL THEN 'concluido' ELSE 'pendente' END AS status
                    FROM pedido p 
                    LEFT JOIN motoboy m ON m.id = p.motoboy_responsavel
                    
                    UNION ALL
                    
                    SELECT p.id, 3 AS evento_ordem, 'Em Entrega' AS evento,
                           CASE 
                               WHEN p.horario_saida IS NOT NULL THEN TO_CHAR(p.horario_saida::timestamp, 'HH24:MI')
                               ELSE '--:--'
                           END AS horario,
                           'Em trânsito' AS local,
                            CASE 
                                WHEN p.status_pedido = 2 THEN 'em_andamento' -- 2 = EmRota: e a entrega atual
                                WHEN p.status_pedido = 3 THEN 'concluido'    -- 3 = Concluido (StatusPedido); 4 agora e Cancelado
                                ELSE 'pendente' 
                            END AS status
                    FROM pedido p
                    
                    UNION ALL
                    
                    SELECT p.id, 4 AS evento_ordem, 'Entregue' AS evento,
                           CASE 
                               WHEN p.horario_entrega IS NOT NULL THEN TO_CHAR(p.horario_entrega::timestamp, 'HH24:MI')
                               ELSE '--:--'
                           END AS horario,
                           p.endereco_entrega AS local,
                            CASE WHEN p.status_pedido = 3 THEN 'concluido' ELSE 'pendente' END AS status -- entregue = Concluido (3), nao 4
                    FROM pedido p
                ) timeline_data
                ORDER BY ""PedidoId"", ""Id"";
            ";

            try
            {
                using var multi = await connection.QueryMultipleAsync(sqlPedidos, new { EstabelecimentoId = estabelecimentoId });

                // Lê todos os pedidos principais
                var pedidosData = (await multi.ReadAsync<dynamic>()).ToList();
                if (!pedidosData.Any())
                    return new List<PedidoCompletoResponse>();

                // Lê todos os itens agrupados por pedido
                var todosItens = (await multi.ReadAsync<dynamic>()).ToList();
                var itensGrouped = todosItens.GroupBy(i => (int)i.PedidoId)
                    .ToDictionary(g => g.Key, g => g.Select(item => new ItemPedidoDto
                    {
                        Id = (int)item.Id,
                        Nome = item.Nome?.ToString() ?? "",
                        Quantidade = (int)item.Quantidade,
                        Valor = (decimal)item.Valor,
                        Tipo = item.Tipo?.ToString() ?? "comida",
                        Observacoes = item.Observacoes?.ToString()
                    }).ToList());
                
                // Lê toda a timeline agrupada por pedido
                var todasTimelines = (await multi.ReadAsync<dynamic>()).ToList();
                var timelinesGrouped = todasTimelines.GroupBy(t => (int)t.PedidoId)
                    .ToDictionary(g => g.Key, g => g.Select(timeline => new TimelineDto
                    {
                        Id = (int)timeline.Id,
                        Evento = timeline.evento?.ToString() ?? "",
                        Horario = timeline.horario?.ToString() ?? "--:--",
                        Local = timeline.local?.ToString() ?? "",
                        Status = timeline.status?.ToString() ?? "pendente"
                    }).ToList());

                // Mapeia todos os pedidos
                var pedidosCompletos = new List<PedidoCompletoResponse>();
                
                foreach (var pedidoData in pedidosData)
                {
                    int pedidoId = (int)pedidoData.id;
                    
                    // Mapeia o status do pedido
                    string statusPedido = MapearStatusPedido((int)pedidoData.status_pedido);
                    
                    // Formata horários
                    string horarioPedido = "--:--";
                    string horarioFormatado = "--:--";
                    string dataPedido = DateTime.UtcNow.ToString("O"); // ISO format
                    
                    if (pedidoData.horario_pedido != null)
                    {
                        if (DateTime.TryParse(pedidoData.horario_pedido.ToString(), out DateTime horario))
                        {
                            horarioPedido = horario.ToString("HH:mm");
                            horarioFormatado = horario.ToString("HH:mm");
                        }
                    }
                    
                    if (pedidoData.DataPedido != null)
                    {
                        if (DateTime.TryParse(pedidoData.DataPedido.ToString(), out DateTime data))
                        {
                            dataPedido = data.ToString("O"); // ISO format
                        }
                    }

                    // Constrói o objeto de resposta
                    var response = new PedidoCompletoResponse
                    {
                        Id = pedidoId,
                        IdIfood = pedidoData.IdIfood?.ToString(),
                        NomeCliente = pedidoData.NomeCliente?.ToString() ?? "",
                        TelefoneCliente = pedidoData.TelefoneCliente?.ToString() ?? "",
                        EnderecoEntrega = pedidoData.EnderecoEntrega?.ToString() ?? "",
                        Bairro = pedidoData.Bairro?.ToString() ?? "",
                        Coordinates = new CoordinatesDto
                        {
                            Lat = Convert.ToDouble(pedidoData.Latitude ?? 0),
                            Lng = Convert.ToDouble(pedidoData.Longitude ?? 0)
                        },
                        ValorTotal = Convert.ToDecimal(pedidoData.ValorTotal ?? 0),
                        TipoPagamento = pedidoData.TipoPagamento?.ToString() ?? "a_receber",
                        StatusPagamento = pedidoData.StatusPagamento?.ToString() ?? "a_receber",
                        Troco = pedidoData.Troco != null ? Convert.ToDecimal(pedidoData.Troco) : null,
                        Itens = itensGrouped.GetValueOrDefault(pedidoId, new List<ItemPedidoDto>()),
                        HorarioPedido = horarioPedido,
                        HorarioFormatado = horarioFormatado,
                        DataPedido = dataPedido,
                        StatusPedido = statusPedido,
                        MotoboyResponsavel = pedidoData.MotoboyResponsavel?.ToString(),
                        DistanciaKm = Convert.ToDouble(pedidoData.DistanciaKm ?? 0),
                        Timeline = timelinesGrouped.GetValueOrDefault(pedidoId, new List<TimelineDto>()),
                        CodigoEntrega = pedidoData.CodigoEntrega?.ToString(),
                        Observacoes = pedidoData.Observacoes?.ToString()
                    };

                    pedidosCompletos.Add(response);
                }

                return pedidosCompletos;
            }
            catch (Exception ex)
            {
                // Log estruturado do erro (implementar logger se necessário)
                Console.WriteLine($"❌ Erro ao buscar todos os pedidos completos: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Mapeia o status numérico do banco para o vocabulario ja usado pelo endpoint
        /// riderlink (consumido por tela do cliente final). Mantem o vocabulario
        /// historico ("disponivel"/"em_entrega"/"entregue"/"cancelado") para nao quebrar
        /// quem ja consome esses valores; apenas corrige a correspondencia com o enum
        /// StatusPedido, que antes estava incorreta (3=Concluido e 4=Cancelado
        /// apareciam ambos mapeados de forma trocada) e nao conhecia o novo Atribuido=5.
        /// </summary>
        private static string MapearStatusPedido(int statusNumerico)
        {
            return StatusPedidoExtensions.FromDbValue(statusNumerico) switch
            {
                StatusPedido.Pendente => "disponivel",
                StatusPedido.Atribuido => "disponivel",
                StatusPedido.EmRota => "em_entrega",
                StatusPedido.Concluido => "entregue",
                StatusPedido.Cancelado => "cancelado",
                StatusPedido.Rascunho => "rascunho",
                _ => "disponivel"
            };
        }
    }
}







