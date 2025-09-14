// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using Dapper;
using Npgsql;

namespace APIBack.Automation.Infra
{
    public class SqlIARespostaRepository : IIARespostaRepository
    {
        private readonly string _connectionString;
        public SqlIARespostaRepository(Microsoft.Extensions.Configuration.IConfiguration config)
        {
            _connectionString = config.GetConnectionString("DefaultConnection")
                                 ?? config["ConnectionStrings:DefaultConnection"]
                                 ?? throw new InvalidOperationException("Connection string 'DefaultConnection' não encontrada.");
        }

        public async Task RegistrarAsync(Guid? idRegra, Guid idConversa, string resposta)
        {
            var agora = DateTime.UtcNow;
            const string sql = @"INSERT INTO ia_respostas (id, id_regra, id_conversa, resposta, data_criacao)
                                 VALUES (@Id, @IdRegra, @IdConversa, @Resposta, @DataCriacao);";
            await using var cx = new NpgsqlConnection(_connectionString);
            await cx.ExecuteAsync(sql, new
            {
                Id = Guid.NewGuid(),
                IdRegra = (object?)idRegra ?? DBNull.Value,
                IdConversa = idConversa,
                Resposta = resposta,
                DataCriacao = agora
            });
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

