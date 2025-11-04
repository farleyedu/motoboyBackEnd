using System;
using System.Collections.Generic;
using APIBack.DTOs;
using APIBack.Repository.Interface;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Repository
{
    public class ReservasRepository : IReservasRepository
    {
        private readonly string _connectionString;

        public ReservasRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        /// <summary>
        /// Obtém reservas de restaurante filtradas por mês/ano/estabelecimento
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
        /// Obtém reservas de barbearia + lista de barbeiros ativos
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
        /// Obtém métricas consolidadas das reservas confirmadas de um dia específico.
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
                  AND status = 'confirmada'
            ";

            var resultado = connection.QuerySingleOrDefault<MetricasDiaDTO>(sql, new
            {
                Data = data.Date
            });

            return resultado ?? new MetricasDiaDTO
            {
                QuantidadeConfirmadas = 0,
                TotalPessoas = 0
            };
        }
    }
}

