// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Automation.Infra
{
    public class SqlWabaPhoneRepository : IWabaPhoneRepository
    {
        private readonly string _connectionString;
        private readonly ILogger<SqlWabaPhoneRepository>? _logger;

        public SqlWabaPhoneRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        }

        public SqlWabaPhoneRepository(IConfiguration configuration, ILogger<SqlWabaPhoneRepository> logger) : this(configuration)
        {
            _logger = logger;
        }

        public async Task<Guid?> ObterIdEstabelecimentoPorPhoneNumberIdAsync(string phoneNumberId)
        {
            if (string.IsNullOrWhiteSpace(phoneNumberId))
                return null;

            var digitsOnly = new string(phoneNumberId.Where(char.IsDigit).ToArray());

            const string sql = @"SELECT id_estabelecimento
                                  FROM waba_phone
                                  WHERE ativo = TRUE
                                    AND (
                                         phone_number_id = @Raw
                                         OR regexp_replace(phone_number_id, '[^0-9]', '', 'g') = @Digits
                                        )
                                  ORDER BY data_atualizacao DESC
                                  LIMIT 1;";

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                var result = await connection.ExecuteScalarAsync<Guid?>(sql, new { Raw = phoneNumberId, Digits = digitsOnly });
                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Erro ao buscar estabelecimento por phone_number_id {PhoneNumberId}", phoneNumberId);
                return null;
            }
        }

        public async Task<Guid?> ObterIdEstabelecimentoPorDisplayPhoneAsync(string displayPhoneNumber)
        {
            if (string.IsNullOrWhiteSpace(displayPhoneNumber))
                return null;

            const string sql = @"
        SELECT id_estabelecimento
        FROM waba_phone
        WHERE display_phone_number = @displayPhoneNumber
          AND ativo = true
        LIMIT 1;";

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                var idEstabelecimento = await connection.QueryFirstOrDefaultAsync<Guid?>(
                    sql,
                    new { displayPhoneNumber });
                return idEstabelecimento;
            }
            catch (Exception ex)
            {
                _logger?.LogError(
                    ex,
                    "Erro ao buscar estabelecimento por display_phone_number {DisplayPhone}",
                    displayPhoneNumber);
                return null;
            }
        }

        public async Task<bool> InserirOuAtualizarAsync(WabaPhone wabaPhone)
        {
            if (wabaPhone == null || string.IsNullOrWhiteSpace(wabaPhone.PhoneNumberId))
                return false;

            var criado = wabaPhone.DataCriacao != default ? DateTime.SpecifyKind(wabaPhone.DataCriacao, DateTimeKind.Utc) : DateTime.UtcNow;
            var atualizado = DateTime.UtcNow;

            const string sql = @"INSERT INTO waba_phone (phone_number_id, id_estabelecimento, ativo, descricao, data_criacao, data_atualizacao)
                                 VALUES (@PhoneNumberId, @IdEstabelecimento, @Ativo, @Descricao, @DataCriacao, @DataAtualizacao)
                                 ON CONFLICT (phone_number_id)
                                 DO UPDATE SET
                                   id_estabelecimento = EXCLUDED.id_estabelecimento,
                                   ativo              = EXCLUDED.ativo,
                                   descricao          = EXCLUDED.descricao,
                                   data_atualizacao   = EXCLUDED.data_atualizacao;";

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                var rows = await connection.ExecuteAsync(sql, new
                {
                    PhoneNumberId = wabaPhone.PhoneNumberId,
                    IdEstabelecimento = wabaPhone.IdEstabelecimento,
                    Ativo = wabaPhone.Ativo,
                    Descricao = (object?)wabaPhone.Descricao,
                    DataCriacao = criado,
                    DataAtualizacao = atualizado
                });
                return rows > 0;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Erro ao inserir/atualizar WabaPhone {PhoneNumberId}", wabaPhone.PhoneNumberId);
                return false;
            }
        }

        public async Task<bool> ExisteAtivoAsync(string phoneNumberId)
        {
            if (string.IsNullOrWhiteSpace(phoneNumberId))
                return false;

            var digitsOnly = new string(phoneNumberId.Where(char.IsDigit).ToArray());

            const string sql = @"SELECT 1
                                  FROM waba_phone
                                  WHERE ativo = TRUE
                                    AND (
                                         phone_number_id = @Raw
                                         OR regexp_replace(phone_number_id, '[^0-9]', '', 'g') = @Digits
                                        )
                                  LIMIT 1;";

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                var existe = await connection.ExecuteScalarAsync<int?>(sql, new { Raw = phoneNumberId, Digits = digitsOnly });
                return existe.HasValue;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Erro ao verificar se WabaPhone esta ativo {PhoneNumberId}", phoneNumberId);
                return false;
            }
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
