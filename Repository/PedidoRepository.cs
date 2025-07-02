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

        public IEnumerable<Pedido> GetPedidos()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            {
                return connection.Query<Pedido>("SELECT * FROM pedido").ToList();
            }
        }

        public IEnumerable<PedidoDTOs> GetPedidosMaps()
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
LEFT JOIN motoboy m ON m.id = p.motoboy_responsavel;
";

                var pedidos = connection.Query<PedidoDTOs, MotoboyDTO, PedidoDTOs>(
                   sql,
                   (pedido, motoboy) =>
                   {
                       pedido.MotoboyResponsavel = motoboy;
                       return pedido;
                   },
                   splitOn: "motoboyid"
               );
                return pedidos;
            }
        }


        public IEnumerable<Pedido> GetPedidosPorMotoboy(int motoboyId)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            {
                var sql = "SELECT *  FROM Pedido WHERE motoboy_responsavel = @motoboyId";
                return connection.Query<Pedido>(sql, new { MotoboyId = motoboyId }).ToList();
            }
        }

        public void InserirPedidosIfood(PedidoCapturado pedido)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open(); // 👈 Importante

            var sql = @"
    INSERT INTO pedido (
        nome_cliente, endereco_entrega, id_ifood, telefone_cliente, data_pedido, 
        status_pedido, horario_entrega, items, value, region,
        latitude, longitude, horario_pedido, previsao_entrega, horario_saida, localizador,
        entrega_rua, entrega_numero, entrega_bairro, entrega_cidade, entrega_estado, entrega_cep,
        documento_cliente, tipo_pagamento
    )
    VALUES (
        @NomeCliente, @EnderecoEntrega, @IdIfood, @TelefoneCliente, @DataPedido, 
        @StatusPedido, @HorarioEntrega, @Items, @Value, @Region,
        @Latitude, @Longitude, @HorarioPedido, @PrevisaoEntrega, @HorarioSaida, @Localizador,
        @EntregaRua, @EntregaNumero, @EntregaBairro, @EntregaCidade, @EntregaEstado, @EntregaCep,
        @DocumentoCliente, @TipoPagamento
    );";

            try
            {
                connection.Execute(sql, new
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
                    TipoPagamento = pedido.TipoPagamento
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Erro ao inserir pedido {pedido.Id}: {ex.Message}");
            }

        }



        public EnviarPedidosParaRotaDTO? GetPedidosId(int id)
        {
            using var connection = new NpgsqlConnection(_connectionString);

            const string sql = @"
        SELECT p.*, m.id as MotoboyId, m.nome as NomeMotoboy, m.avatar, m.status
        FROM pedido p
        LEFT JOIN motoboy m ON m.id = p.motoboy_responsavel
        WHERE p.id = @Id";

            var result = connection.Query<Pedido, MotoboyDTO, (Pedido, MotoboyDTO)>(
                sql,
                (pedido, motoboy) => (pedido, motoboy),
                new { Id = id },
                splitOn: "MotoboyId"
            );

            var tuple = result.FirstOrDefault();
            if (tuple.Item1 == null) return null;

            return new EnviarPedidosParaRotaDTO
            {
                PedidosIds = tuple.Item1.Id != null ? new List<int> { tuple.Item1.Id.Value } : new List<int>(),
                MotoboyResponsavel = tuple.Item2,
                StatusPedido = tuple.Item1.StatusPedido != null ? (StatusPedido)tuple.Item1.StatusPedido : StatusPedido.Pendente,
                HorarioSaida = DateTime.UtcNow.ToString("HH:mm:ss")
            };
        }





        public IEnumerable<Pedido> CriarPedido()
        {
            throw new NotImplementedException();
        }
        public async Task AtribuirMotoboy(EnviarPedidosParaRotaDTO dto)
        {
            using var connection = new NpgsqlConnection(_connectionString);

            const string sql = @"
        UPDATE pedido
        SET 
            status_pedido = @StatusPedido,
            motoboy_responsavel = @MotoboyResponsavel,
            horario_saida = @HorarioSaida
        WHERE id = ANY(@PedidosIds)
    ";

            try
            {
                await connection.ExecuteAsync(sql, new
                {
                    StatusPedido = (int)dto.StatusPedido,
                    MotoboyResponsavel = dto.MotoboyResponsavel.Id,
                    HorarioSaida = string.IsNullOrWhiteSpace(dto.HorarioSaida)
                        ? DateTime.UtcNow
                        : DateTime.Parse(dto.HorarioSaida),
                    PedidosIds = dto.PedidosIds.ToArray()
                });

            }
            catch (Exception ex)
            {
                // Logar o erro se necessário
                throw;
            }
        }

        public IEnumerable<Pedido> CancelarPedido()
        {
            throw new NotImplementedException();
        }
        public IEnumerable<Pedido> FinalizarPedido()
        {
            throw new NotImplementedException();
        }
        public IEnumerable<Pedido> AlteraPedido(int Id, Pedido pedido)
        {
            throw new NotImplementedException();
        }
    }
}
