// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using Dapper;
using Npgsql;

namespace APIBack.Automation.Infra
{
    public class SqlAgenteRepository : IAgenteRepository
    {
        private readonly string _connectionString;

        public SqlAgenteRepository(Microsoft.Extensions.Configuration.IConfiguration config)
        {
            _connectionString = config.GetConnectionString("DefaultConnection")
                                 ?? config["ConnectionStrings:DefaultConnection"]!;
        }

        public async Task<long?> ObterTelegramChatIdPorAgenteIdAsync(int agenteId)
        {
            const string sql = "SELECT telegramchatid FROM agentes WHERE id = @Id LIMIT 1;";
            await using var cx = new NpgsqlConnection(_connectionString);
            var chatId = await cx.ExecuteScalarAsync<long?>(sql, new { Id = agenteId });
            return chatId;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

