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
    public class EstabelecimentoServicoRepository : IEstabelecimentoServicoRepository
    {
        private readonly string _connectionString;

        public EstabelecimentoServicoRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        public async Task<long> AdicionarAsync(EstabelecimentoServico entity)
        {
            const string sql = @"INSERT INTO estabelecimento_servicos (
                                        id_estabelecimento,
                                        nome,
                                        descricao,
                                        tipo,
                                        duracao_minutos,
                                        valor,
                                        ativo)
                                  VALUES (
                                        @IdEstabelecimento,
                                        @Nome,
                                        @Descricao,
                                        @Tipo,
                                        @DuracaoMinutos,
                                        @Valor,
                                        @Ativo)
                                  RETURNING id;";

            using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteScalarAsync<long>(sql, entity);
        }

        public async Task<EstabelecimentoServico?> BuscarPorIdAsync(long id)
        {
            const string sql = @"SELECT
                                        id AS Id,
                                        id_estabelecimento AS IdEstabelecimento,
                                        nome AS Nome,
                                        descricao AS Descricao,
                                        tipo AS Tipo,
                                        duracao_minutos AS DuracaoMinutos,
                                        valor AS Valor,
                                        ativo AS Ativo,
                                        data_criacao AS DataCriacao,
                                        data_atualizacao AS DataAtualizacao
                                   FROM estabelecimento_servicos
                                   WHERE id = @Id;";

            using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<EstabelecimentoServico>(sql, new { Id = id });
        }

        public async Task<IEnumerable<EstabelecimentoServico>> BuscarTodosAsync()
        {
            const string sql = @"SELECT
                                        id AS Id,
                                        id_estabelecimento AS IdEstabelecimento,
                                        nome AS Nome,
                                        descricao AS Descricao,
                                        tipo AS Tipo,
                                        duracao_minutos AS DuracaoMinutos,
                                        valor AS Valor,
                                        ativo AS Ativo,
                                        data_criacao AS DataCriacao,
                                        data_atualizacao AS DataAtualizacao
                                   FROM estabelecimento_servicos
                                   ORDER BY id;";

            using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryAsync<EstabelecimentoServico>(sql);
        }

        public async Task<int> AtualizarAsync(EstabelecimentoServico entity)
        {
            entity.DataAtualizacao = DateTime.UtcNow;

            const string sql = @"UPDATE estabelecimento_servicos
                                   SET id_estabelecimento = @IdEstabelecimento,
                                       nome = @Nome,
                                       descricao = @Descricao,
                                       tipo = @Tipo,
                                       duracao_minutos = @DuracaoMinutos,
                                       valor = @Valor,
                                       ativo = @Ativo,
                                       data_atualizacao = @DataAtualizacao
                                   WHERE id = @Id;";

            using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteAsync(sql, entity);
        }

        public async Task<int> ExcluirAsync(long id)
        {
            const string sql = "DELETE FROM estabelecimento_servicos WHERE id = @Id;";

            using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteAsync(sql, new { Id = id });
        }
    }
}
