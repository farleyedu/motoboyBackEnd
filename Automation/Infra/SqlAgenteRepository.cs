// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
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
            const string sql = "SELECT NULLIF(telegramchatid, '')::bigint FROM agentes WHERE id = @Id LIMIT 1;";
            await using var cx = new NpgsqlConnection(_connectionString);
            var chatId = await cx.ExecuteScalarAsync<long?>(sql, new { Id = agenteId });
            return chatId;
        }

        public async Task<HandoverAgentDto?> ObterAgentePorIdAsync(int agenteId)
        {
            const string sql = @"SELECT a.id,
                                          u.nome,
                                          NULLIF(a.telegramchatid, '')::bigint AS TelegramChatId
                                     FROM agentes a
                                     LEFT JOIN usuario u ON u.id = a.usuarioid
                                    WHERE a.id = @Id
                                    LIMIT 1;";
            await using var cx = new NpgsqlConnection(_connectionString);
            return await cx.QueryFirstOrDefaultAsync<HandoverAgentDto>(sql, new { Id = agenteId });
        }

        public async Task<HandoverAgentDto?> ObterAgenteSuporteAsync()
        {
            const string sql = @"SELECT a.id,
                                          u.nome,
                                          NULLIF(a.telegramchatid, '')::bigint AS TelegramChatId
                                     FROM agentes a
                                     LEFT JOIN usuario u ON u.id = a.usuarioid
                                    WHERE a.funcao = @Funcao::agente_funcao_enum
                                    ORDER BY a.id
                                    LIMIT 1;";
            await using var cx = new NpgsqlConnection(_connectionString);
            return await cx.QueryFirstOrDefaultAsync<HandoverAgentDto>(sql, new { Funcao = "Suporte" });
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

