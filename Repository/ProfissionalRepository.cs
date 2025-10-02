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
    public class ProfissionalRepository : IProfissionalRepository
    {
        private readonly string _connectionString;

        public ProfissionalRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        public async Task<long> AdicionarAsync(Profissional entity)
        {
            const string sql = @"INSERT INTO profissionais (
                                        id_usuario,
                                        id_estabelecimento,
                                        ativo)
                                  VALUES (
                                        @IdUsuario,
                                        @IdEstabelecimento,
                                        @Ativo)
                                  RETURNING id;";

            using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteScalarAsync<long>(sql, entity);
        }

        public async Task<Profissional?> BuscarPorIdAsync(long id)
        {
            const string sql = @"SELECT
                                        id AS Id,
                                        id_usuario AS IdUsuario,
                                        id_estabelecimento AS IdEstabelecimento,
                                        ativo AS Ativo,
                                        data_criacao AS DataCriacao,
                                        data_atualizacao AS DataAtualizacao
                                   FROM profissionais
                                   WHERE id = @Id;";

            using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<Profissional>(sql, new { Id = id });
        }

        public async Task<IEnumerable<Profissional>> BuscarTodosAsync()
        {
            const string sql = @"SELECT
                                        id AS Id,
                                        id_usuario AS IdUsuario,
                                        id_estabelecimento AS IdEstabelecimento,
                                        ativo AS Ativo,
                                        data_criacao AS DataCriacao,
                                        data_atualizacao AS DataAtualizacao
                                   FROM profissionais
                                   ORDER BY id;";

            using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryAsync<Profissional>(sql);
        }

        public async Task<int> AtualizarAsync(Profissional entity)
        {
            entity.DataAtualizacao = DateTime.UtcNow;

            const string sql = @"UPDATE profissionais
                                   SET id_usuario = @IdUsuario,
                                       id_estabelecimento = @IdEstabelecimento,
                                       ativo = @Ativo,
                                       data_atualizacao = @DataAtualizacao
                                   WHERE id = @Id;";

            using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteAsync(sql, entity);
        }

        public async Task<int> ExcluirAsync(long id)
        {
            const string sql = "DELETE FROM profissionais WHERE id = @Id;";

            using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteAsync(sql, new { Id = id });
        }
    }
}
