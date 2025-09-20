// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Dapper;
using Npgsql;

namespace APIBack.Automation.Infra
{
    public class SqlIARegraRepository : IIARegraRepository
    {
        private readonly string _connectionString;

        public SqlIARegraRepository(Microsoft.Extensions.Configuration.IConfiguration config)
        {
            _connectionString = config.GetConnectionString("DefaultConnection")
                                 ?? config["ConnectionStrings:DefaultConnection"]
                                 ?? throw new InvalidOperationException("Connection string 'DefaultConnection' não encontrada.");
        }

        public async Task<string?> ObterContextoAtivoAsync(Guid idEstabelecimento)
        {
            const string sql = @"SELECT contexto
                                   FROM ia_regras
                                  WHERE id_estabelecimento = @IdEstabelecimento
                                    AND ativo = TRUE
                                  ORDER BY data_atualizacao DESC, data_criacao DESC
                                  LIMIT 1;";
            await using var cx = new NpgsqlConnection(_connectionString);
            return await cx.ExecuteScalarAsync<string?>(sql, new { IdEstabelecimento = idEstabelecimento });
        }


        public async Task<Guid> CriarAsync(Guid idEstabelecimento, string contexto)
        {
            var id = Guid.NewGuid();
            const string sql = @"INSERT INTO ia_regras (id, id_estabelecimento, contexto, ativo, data_criacao, data_atualizacao)
                                 VALUES (@Id, @IdEstabelecimento, @Contexto, TRUE, NOW(), NOW());";
            await using var cx = new NpgsqlConnection(_connectionString);
            await cx.ExecuteAsync(sql, new { Id = id, IdEstabelecimento = idEstabelecimento, Contexto = contexto });
            return id;
        }

        public async Task<bool> ExcluirAsync(Guid id)
        {
            const string sql = "DELETE FROM ia_regras WHERE id = @Id;";
            await using var cx = new NpgsqlConnection(_connectionString);
            var rows = await cx.ExecuteAsync(sql, new { Id = id });
            return rows > 0;
        }

        public async Task<IEnumerable<IARegra>> ListaregrasAsync(Guid idEstabelecimento)
        {
            const string sql = @"SELECT id                 AS ""Id"",
                                         id_estabelecimento AS ""IdEstabelecimento"",
                                         contexto           AS ""Contexto"",
                                         ativo              AS ""Ativo"",
                                         data_criacao       AS ""DataCriacao"",
                                         data_atualizacao   AS ""DataAtualizacao""
                                    FROM ia_regras
                                   WHERE id_estabelecimento = @IdEstabelecimento
                                   ORDER BY data_atualizacao DESC, data_criacao DESC;";
            await using var cx = new NpgsqlConnection(_connectionString);
            return await cx.QueryAsync<IARegra>(sql, new { IdEstabelecimento = idEstabelecimento });
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================


