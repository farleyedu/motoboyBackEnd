using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs;
using APIBack.Repository.Interface;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Repository
{
    public class ReservasRepository : IReservasRepository
    {
        private static readonly string[] StatusesConsiderados = new[]
        {
            "confirmado",
            "confirmada",
            "concluído",
            "concluido"
        };

        private readonly string _connectionString;

        public ReservasRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        /// <summary>
        /// Obtem reservas de restaurante filtradas por mes/ano/estabelecimento.
        /// </summary>
        public IEnumerable<dynamic> GetReservasRestaurante(int month, int year, Guid estabelecimentoId)
        {
            using var connection = new NpgsqlConnection(_connectionString);

            const string sql = @"
                SELECT 
                    id,
                    data_reserva,
                    hora_inicio,
                    hora_fim,
                    nome_cliente_reserva,
                    qtd_pessoas,
                    status,
                    observacoes
                FROM reservas
                WHERE id_estabelecimento = @EstabelecimentoId
                    AND EXTRACT(MONTH FROM data_reserva) = @Month
                    AND EXTRACT(YEAR FROM data_reserva) = @Year
                ORDER BY data_reserva, hora_inicio
            ";

            return connection.Query<dynamic>(sql, new
            {
                EstabelecimentoId = estabelecimentoId,
                Month = month,
                Year = year
            });
        }

        /// <summary>
        /// Obtem reservas de barbearia e a lista de barbeiros ativos.
        /// </summary>
        public (IEnumerable<dynamic> reservations, IEnumerable<dynamic> barbers) GetReservasBarbearia(int month, int year, Guid estabelecimentoId)
        {
            using var connection = new NpgsqlConnection(_connectionString);

            const string sqlReservas = @"
                SELECT 
                    r.id,
                    r.data_reserva,
                    r.hora_inicio,
                    r.hora_fim,
                    r.nome_cliente_reserva,
                    r.status,
                    r.id_profissional,
                    u.nome AS ""nomeProfissional""
                FROM reservas r
                LEFT JOIN profissionais p ON r.id_profissional = p.id
                LEFT JOIN usuario u ON p.id_usuario = u.id
                WHERE r.id_estabelecimento = @EstabelecimentoId
                    AND EXTRACT(MONTH FROM r.data_reserva) = @Month
                    AND EXTRACT(YEAR FROM r.data_reserva) = @Year
                ORDER BY r.data_reserva, r.hora_inicio
            ";

            const string sqlBarbeiros = @"
                SELECT 
                    p.id,
                    u.nome,
                    p.ativo
                FROM profissionais p
                JOIN usuario u ON p.id_usuario = u.id
                WHERE p.id_estabelecimento = @EstabelecimentoId
                    AND p.ativo = true
                ORDER BY u.nome
            ";

            var parameters = new
            {
                EstabelecimentoId = estabelecimentoId,
                Month = month,
                Year = year
            };

            var reservations = connection.Query<dynamic>(sqlReservas, parameters);
            var barbers = connection.Query<dynamic>(sqlBarbeiros, new { EstabelecimentoId = estabelecimentoId });

            return (reservations, barbers);
        }

        /// <summary>
        /// Conta total de reservas e pessoas confirmadas em um período específico.
        /// </summary>
        public (int totalReservas, int totalPessoas) ContarReservasPorPeriodo(DateTime dataInicio, DateTime dataFim, Guid estabelecimentoId)
        {
            using var connection = new NpgsqlConnection(_connectionString);

            const string sql = @"
                SELECT 
                    COUNT(*) AS total_reservas,
                    COALESCE(SUM(qtd_pessoas), 0) AS total_pessoas
                FROM reservas
                WHERE id_estabelecimento = @EstabelecimentoId
                  AND data_reserva BETWEEN @DataInicio AND @DataFim
                  AND status::text = ANY(@StatusList)
            ";

            var resultado = connection.QuerySingleOrDefault<(int total_reservas, int total_pessoas)>(sql, new
            {
                EstabelecimentoId = estabelecimentoId,
                DataInicio = dataInicio,
                DataFim = dataFim,
                StatusList = StatusesConsiderados
            });

            return resultado == default ? (0, 0) : (resultado.total_reservas, resultado.total_pessoas);
        }

        /// <summary>
        /// Obtem metricas consolidadas das reservas confirmadas de um dia especifico.
        /// </summary>
        public MetricasDiaDTO GetMetricasDia(DateTime data)
        {
            using var connection = new NpgsqlConnection(_connectionString);

            const string sql = @"
                SELECT 
                    COUNT(*) AS quantidadeConfirmadas,
                    COALESCE(SUM(qtd_pessoas), 0) AS totalPessoas
                FROM reservas
                WHERE data_reserva = @Data
                  AND status::text = ANY(@StatusList)
            ";

            var resultado = connection.QuerySingleOrDefault<MetricasDiaDTO>(sql, new
            {
                Data = data.Date,
                StatusList = StatusesConsiderados
            });

            return resultado ?? new MetricasDiaDTO
            {
                QuantidadeConfirmadas = 0,
                TotalPessoas = 0
            };
        }

        public async Task<bool> AtualizarStatusAsync(int id, string novoStatus)
        {
            await using var connection = new NpgsqlConnection(_connectionString);

            const string sql = @"
                UPDATE reservas
                   SET status = @Status::status_reserva,
                       data_atualizacao = @Atualizacao
                 WHERE id = @Id;
            ";

            var linhasAfetadas = await connection.ExecuteAsync(sql, new
            {
                Id = id,
                Status = novoStatus,
                Atualizacao = DateTime.UtcNow
            });

            return linhasAfetadas > 0;
        }

        public async Task<IEnumerable<dynamic>> ListarPorDiaAsync(DateOnly data)
        {
            await using var connection = new NpgsqlConnection(_connectionString);

            const string sql = @"
                SELECT 
                    r.id,
                    r.data_reserva,
                    r.hora_inicio,
                    r.hora_fim,
                    r.nome_cliente_reserva,
                    r.qtd_pessoas,
                    r.status,
                    r.observacoes,
                    COALESCE(c.telefone_e164, '') AS telefone_cliente
                FROM reservas r
                LEFT JOIN clientes c ON c.id = r.id_cliente
                WHERE r.data_reserva = @Data
                ORDER BY r.hora_inicio;
            ";

            return await connection.QueryAsync(sql, new
            {
                Data = data.ToDateTime(TimeOnly.MinValue)
            });
        }
    }
}


