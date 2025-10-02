using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Model;
using APIBack.Repository.Interface;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Repository
{
    public class ProfissionalHorarioRepository : IProfissionalHorarioRepository
    {
        private readonly string _connectionString;

        public ProfissionalHorarioRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        public async Task<long> AdicionarAsync(ProfissionalHorario entity)
        {
            const string sql = @"INSERT INTO profissional_horarios (
                                        id_profissional,
                                        dia_semana,
                                        hora_inicio,
                                        hora_fim,
                                        ativo)
                                  VALUES (
                                        @IdProfissional,
                                        @DiaSemana,
                                        @HoraInicio,
                                        @HoraFim,
                                        @Ativo)
                                  RETURNING id;";

            using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteScalarAsync<long>(sql, entity);
        }

        public async Task<ProfissionalHorario?> BuscarPorIdAsync(long id)
        {
            const string sql = @"SELECT
                                        id AS Id,
                                        id_profissional AS IdProfissional,
                                        dia_semana AS DiaSemana,
                                        hora_inicio AS HoraInicio,
                                        hora_fim AS HoraFim,
                                        ativo AS Ativo,
                                        data_criacao AS DataCriacao,
                                        data_atualizacao AS DataAtualizacao
                                   FROM profissional_horarios
                                   WHERE id = @Id;";

            using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<ProfissionalHorario>(sql, new { Id = id });
        }

        public async Task<IEnumerable<ProfissionalHorario>> BuscarTodosAsync()
        {
            const string sql = @"SELECT
                                        id AS Id,
                                        id_profissional AS IdProfissional,
                                        dia_semana AS DiaSemana,
                                        hora_inicio AS HoraInicio,
                                        hora_fim AS HoraFim,
                                        ativo AS Ativo,
                                        data_criacao AS DataCriacao,
                                        data_atualizacao AS DataAtualizacao
                                   FROM profissional_horarios
                                   ORDER BY id;";

            using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryAsync<ProfissionalHorario>(sql);
        }

        public async Task<int> AtualizarAsync(ProfissionalHorario entity)
        {
            entity.DataAtualizacao = DateTime.UtcNow;

            const string sql = @"UPDATE profissional_horarios
                                   SET id_profissional = @IdProfissional,
                                       dia_semana = @DiaSemana,
                                       hora_inicio = @HoraInicio,
                                       hora_fim = @HoraFim,
                                       ativo = @Ativo,
                                       data_atualizacao = @DataAtualizacao
                                   WHERE id = @Id;";

            using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteAsync(sql, entity);
        }

        public async Task<int> ExcluirAsync(long id)
        {
            const string sql = "DELETE FROM profissional_horarios WHERE id = @Id;";

            using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteAsync(sql, new { Id = id });
        }
    }
}
